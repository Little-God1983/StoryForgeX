using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Research;

/// <summary>
/// Research through Claude CLI, non-interactive. Claude gets the StoryForge research server and
/// nothing else: no built-in tools (no web search, no files, no shell), no other MCP servers, none
/// of the user's settings or hooks. Corrections continue the same session, so the pages it read
/// are still in front of it.
/// </summary>
internal sealed class ClaudeCliResearchAgent(
    SettingsStore settings,
    IStreamingProcess process,
    IOptions<StoryForgeEngineOptions> options,
    TimeProvider clock) : IResearchAgent
{
    public const string ServerName = "storyforge";

    internal const string SystemPrompt =
        """
        You are the research step of StoryForge X, which makes narrated videos. You find facts in the
        project's sources with the search and fetch tools, and hand back a fact sheet. The script is
        written from the fact sheet alone, so it must hold everything the video needs, and nothing you
        did not read in a fetched page. If the sources do not cover something, leave it out; never
        fill a gap from memory.
        """;

    public async Task<ResearchAnswer> AskAsync(
        ResearchRequest request,
        ResearchAnswer? previous,
        IReadOnlyList<string> problems,
        IProgress<ActivityLine> activity,
        CancellationToken cancellationToken)
    {
        var cli = (await settings.LoadAsync(cancellationToken)).ClaudeCli;
        if (string.IsNullOrWhiteSpace(cli.Executable))
        {
            throw new StageFailedException("Claude CLI is not set up: set its executable in Settings.");
        }
        var server = options.Value.EffectiveResearchServerPath;
        if (!File.Exists(server))
        {
            throw new StageFailedException($"The research server is missing ({server}). Reinstall StoryForge X.");
        }

        // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
        var folder = Path.Combine(options.Value.DataDirectory, "research", request.ProjectId.ToString("N"));
        Directory.CreateDirectory(folder);
        var mcpConfig = Path.Combine(folder, "mcp.json");
        var systemPrompt = Path.Combine(folder, "system.md");
        await File.WriteAllTextAsync(mcpConfig, McpConfig(server, request.Sources), cancellationToken);
        await File.WriteAllTextAsync(systemPrompt, SystemPrompt, cancellationToken);

        var pages = new ResearchPages();
        if (previous is not null)
        {
            pages.AddAll(previous.Pages);
        }
        var stream = new ClaudeStream(pages, activity, clock);
        var session = previous?.Session;
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, session is null ? "researching the brief" : "fixing the fact sheet"));

        StreamingRunResult result;
        try
        {
            result = await process.RunAsync(
                cli.Executable.Trim(),
                Arguments(mcpConfig, systemPrompt, request.Model, session),
                folder,
                session is null ? FirstPrompt(request) : CorrectionPrompt(problems),
                stream.Read,
                TimeSpan.FromSeconds(Math.Max(60, cli.TimeoutSeconds)),
                cancellationToken);
        }
        catch (ExecutableNotFoundException)
        {
            throw new StageFailedException($"Claude CLI was not found ({cli.Executable.Trim()}). Check its executable in Settings.");
        }
        catch (TimeoutException)
        {
            throw new StageFailedException(
                $"The research took longer than {Math.Max(60, cli.TimeoutSeconds) / 60.0:0.#} min and was stopped. Settings › Claude CLI sets the time limit.");
        }
        catch (ArgumentException ex)
        {
            throw new StageFailedException(ex.Message);
        }
        catch (Win32Exception ex)
        {
            throw new StageFailedException($"Claude CLI could not be started: {ex.Message}");
        }

        if (stream.ServerProblem is { } serverProblem)
        {
            throw new StageFailedException($"The research server did not start ({serverProblem}), so nothing could be searched.");
        }
        if (stream.Output is null)
        {
            var reason = stream.Error ?? FirstLine(result.StandardError);
            throw new StageFailedException(result.ExitCode == 0 && stream.Error is null
                ? "Claude CLI finished without a fact sheet."
                : $"Claude CLI stopped: {(reason.Length > 0 ? reason : $"exit code {result.ExitCode}")}");
        }
        if (stream.CostUsd is { } cost)
        {
            activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model,
                string.Create(CultureInfo.InvariantCulture, $"answer received · {pages.Count} pages read · ${cost:0.00}")));
        }
        return new ResearchAnswer(stream.Session, Read(stream.Output.Value), pages);
    }

    internal static IReadOnlyList<string> Arguments(string mcpConfig, string systemPrompt, string model, string? session)
    {
        List<string> arguments =
        [
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            // No built-in tools at all: the research server is the only way to the web.
            "--tools", "",
            "--strict-mcp-config",
            "--mcp-config", mcpConfig,
            "--allowedTools", $"{ClaudeStream.SearchTool},{ClaudeStream.FetchTool}",
            "--permission-prompts", "none",
            // None of the user's settings, so none of their hooks or plugins run in the research.
            "--setting-sources", "",
            "--system-prompt-file", systemPrompt,
            "--json-schema", ResearchSchema.Json,
        ];
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.AddRange(["--model", model.Trim()]);
        }
        if (session is not null)
        {
            arguments.AddRange(["--resume", session]);
        }
        return arguments;
    }

    internal static string McpConfig(string server, IReadOnlyList<string> sources) =>
        JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new
                {
                    type = "stdio",
                    command = server,
                    args = sources.SelectMany(source => new[] { "--source", source }).ToArray(),
                },
            },
        });

    internal static string FirstPrompt(ResearchRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"Research this brief for a narrated video of about {request.TargetSeconds / 60}:{request.TargetSeconds % 60:00} in {request.Language}.");
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        prompt.AppendLine();
        prompt.AppendLine("The sources you can search and read (nothing else can be reached):");
        foreach (var source in request.Sources)
        {
            prompt.AppendLine($"- {source}");
        }
        prompt.AppendLine();
        prompt.AppendLine(
            $"""
            How to work:
            1. Search the sources, then fetch the pages that matter and read them. Then follow up: search for the people, places and terms those pages name, and read those pages too. Read before you write.
            2. Every fact comes from a page you fetched in this session. Its sourceUrl is that page's URL as the fetch answer gives it after "Page:".
            3. Its quote is the passage the fact rests on, copied word for word from the fetched text: one to three sentences, not changed, shortened or translated.
            4. One claim per fact. Write the statement short and plain, in {request.Language}.
            5. Collect what the script could need: names, places, dates, numbers, what happened and why it matters. 10 to 30 facts is usual.
            """);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("From the research profile:");
            prompt.AppendLine(request.Instructions.Trim());
        }
        return prompt.ToString();
    }

    internal static string CorrectionPrompt(IReadOnlyList<string> problems)
    {
        var prompt = new StringBuilder("The fact sheet has these problems:\n");
        foreach (var problem in problems)
        {
            prompt.AppendLine($"- {problem}");
        }
        prompt.AppendLine();
        prompt.AppendLine("Fix them and hand back the whole fact sheet again. Fetch pages again if you need to.");
        return prompt.ToString();
    }

    private static ResearchOutput? Read(JsonElement output)
    {
        try
        {
            return ResearchSchema.Read(output);
        }
        catch (JsonException)
        {
            return null;   // FactSheetCheck reports it as "no fact sheet", and the model gets to try again
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
