using System.ComponentModel;
using System.Text.Json;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Research;

/// <summary>One question to Claude CLI, answered as JSON in the given schema.</summary>
/// <param name="Folder">The working folder; a correction resumes the session, and Claude CLI finds sessions by folder.</param>
/// <param name="Model">The model to use; empty means the provider's default.</param>
/// <param name="Session">The session to continue (a correction); null for a new one.</param>
/// <param name="McpConfig">The MCP servers as JSON, or null for none at all.</param>
/// <param name="AllowedTools">The only tools Claude may use; empty for none.</param>
/// <param name="What">What the call is for, in messages: "research", "the script".</param>
internal sealed record ClaudeCall(
    string Folder,
    string SystemPrompt,
    string Prompt,
    string SchemaJson,
    string Model,
    string? Session,
    string? McpConfig,
    IReadOnlyList<string> AllowedTools,
    string What);

/// <summary>Claude CLI's answer: the JSON in the asked schema, and the session a correction continues.</summary>
internal sealed record ClaudeReply(string? Session, JsonElement Output, decimal? CostUsd);

/// <summary>
/// Claude CLI, non-interactive, with nothing but what the call allows: no built-in tools (no web
/// search, files or shell), no MCP servers but the call's own, none of the user's settings, hooks
/// or plugins. The prompt goes in through standard input and long texts through files, because
/// npm's claude.cmd runs through cmd.exe.
/// </summary>
internal sealed class ClaudeCli(SettingsStore settings, IStreamingProcess process)
{
    /// <exception cref="StageFailedException">Claude CLI could not be asked, or ended without an answer.</exception>
    public async Task<ClaudeReply> AskAsync(ClaudeCall call, ClaudeStream stream, CancellationToken cancellationToken)
    {
        var cli = (await settings.LoadAsync(cancellationToken)).ClaudeCli;
        if (string.IsNullOrWhiteSpace(cli.Executable))
        {
            throw new StageFailedException("Claude CLI is not set up: set its executable in Settings.");
        }

        Directory.CreateDirectory(call.Folder);
        var systemPrompt = Path.Combine(call.Folder, "system.md");
        await File.WriteAllTextAsync(systemPrompt, call.SystemPrompt, cancellationToken);
        string? mcpConfig = null;
        if (call.McpConfig is not null)
        {
            mcpConfig = Path.Combine(call.Folder, "mcp.json");
            await File.WriteAllTextAsync(mcpConfig, call.McpConfig, cancellationToken);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(60, cli.TimeoutSeconds));
        StreamingRunResult result;
        try
        {
            result = await process.RunAsync(
                cli.Executable.Trim(),
                Arguments(mcpConfig, call.AllowedTools, systemPrompt, call.SchemaJson, call.Model, call.Session),
                call.Folder,
                call.Prompt,
                stream.Read,
                timeout,
                cancellationToken);
        }
        catch (ExecutableNotFoundException)
        {
            throw new StageFailedException($"Claude CLI was not found ({cli.Executable.Trim()}). Check its executable in Settings.");
        }
        catch (TimeoutException)
        {
            throw new StageFailedException(
                $"Claude CLI took longer than {timeout.TotalMinutes:0.#} min on {call.What} and was stopped. Settings › Claude CLI sets the time limit.");
        }
        catch (ArgumentException ex)
        {
            throw new StageFailedException(ex.Message);
        }
        catch (Win32Exception ex)
        {
            throw new StageFailedException($"Claude CLI could not be started: {ex.Message}");
        }

        if (stream.Output is not { } output)
        {
            // Only then: a server still "pending" at the start may have connected later and done its work.
            if (call.McpConfig is not null && stream.ServerProblem is { } serverProblem)
            {
                throw new StageFailedException($"The research server did not start ({serverProblem}), so nothing could be searched.");
            }
            var reason = stream.Error ?? FirstLine(result.StandardError);
            throw new StageFailedException(result.ExitCode == 0 && stream.Error is null
                ? $"Claude CLI finished {call.What} without an answer."
                : $"Claude CLI stopped: {(reason.Length > 0 ? reason : $"exit code {result.ExitCode}")}");
        }
        return new ClaudeReply(stream.Session, output, stream.CostUsd);
    }

    /// <param name="mcpConfig">The MCP config file, or null for no MCP servers at all.</param>
    internal static IReadOnlyList<string> Arguments(
        string? mcpConfig, IReadOnlyList<string> allowedTools, string systemPrompt, string schema, string model, string? session)
    {
        List<string> arguments =
        [
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            // No built-in tools at all: no web search, no files, no shell.
            "--tools", "",
            "--strict-mcp-config",
        ];
        if (mcpConfig is not null)
        {
            arguments.AddRange(["--mcp-config", mcpConfig]);
        }
        if (allowedTools.Count > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', allowedTools)]);
        }
        arguments.AddRange(
        [
            "--permission-prompts", "none",
            // None of the user's settings, so none of their hooks or plugins run in here.
            "--setting-sources", "",
            "--system-prompt-file", systemPrompt,
            "--json-schema", schema,
        ]);
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

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
