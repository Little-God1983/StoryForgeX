using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Script;

/// <summary>What the script is written from: the brief, the approved fact sheet and the script profile.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record ScriptRequest(
    Guid ProjectId,
    string Brief,
    FactSheet Facts,
    string Instructions,
    string Model,
    string Language,
    int TargetSeconds);

/// <summary>One answer of the script model.</summary>
/// <param name="Session">Where a correction continues the same conversation; null if it cannot.</param>
internal sealed record ScriptAnswer<T>(string? Session, T? Output);

/// <summary>The model that writes the script. It has no tools: the facts in the prompt are all it knows.</summary>
internal interface IScriptAgent
{
    /// <summary>The whole script; with a session and problems, the same conversation fixes its last answer.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<ScriptOutput>> WriteAsync(
        ScriptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);

    /// <summary>One segment again, fitting into the rest of the script as it stands.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<ScriptPart>> RewriteAsync(
        ScriptRequest request, IReadOnlyList<Segment> script, string segmentId, string? session, IReadOnlyList<string> problems,
        IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

/// <summary>The script through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliScriptAgent(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IScriptAgent
{
    internal const string SystemPrompt =
        """
        You are the script writer of StoryForge X, which makes narrated videos. You write the narration
        from a fact sheet and nothing else: every claim in it comes from a fact in the list, and you say
        which facts each part uses. Framing, questions and transitions are yours; facts are not. You
        write for the ear: short sentences, numbers as they are spoken, no lists or headings.
        """;

    internal static string Schema { get; } = OutputSchema.Json<ScriptOutput>();

    internal static string SegmentSchema { get; } = OutputSchema.Json<ScriptPart>();

    public async Task<ScriptAnswer<ScriptOutput>> WriteAsync(
        ScriptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        Report(activity, session is null ? "writing the script from the fact sheet" : "fixing the script");
        var reply = await claude.AskAsync(
            Call(request, session is null ? FirstPrompt(request) : CorrectionPrompt(problems, "the whole script"), Schema, session),
            new ClaudeStream(new ResearchPages(), activity, clock, "writing the script"),
            cancellationToken);
        ReportCost(activity, reply);
        return new ScriptAnswer<ScriptOutput>(reply.Session, Read<ScriptOutput>(reply.Output));
    }

    public async Task<ScriptAnswer<ScriptPart>> RewriteAsync(
        ScriptRequest request, IReadOnlyList<Segment> script, string segmentId, string? session, IReadOnlyList<string> problems,
        IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        Report(activity, session is null ? $"writing {segmentId} again" : $"fixing {segmentId}");
        var reply = await claude.AskAsync(
            Call(request, session is null ? SegmentPrompt(request, script, segmentId) : CorrectionPrompt(problems, $"segment {segmentId}"), SegmentSchema, session),
            new ClaudeStream(new ResearchPages(), activity, clock, $"writing {segmentId}"),
            cancellationToken);
        ReportCost(activity, reply);
        return new ScriptAnswer<ScriptPart>(reply.Session, Read<ScriptPart>(reply.Output));
    }

    internal static string FirstPrompt(ScriptRequest request)
    {
        var prompt = new StringBuilder();
        var wpm = ScriptSheet.WordsPerMinute(request.Language);
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"Write the narration for a video of about {ScriptCheck.Clock(request.TargetSeconds)} in {request.Language}: about {request.TargetSeconds * wpm / 60} words in all, at {wpm} words a minute.");
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        prompt.AppendLine();
        AppendFacts(prompt, request.Facts);
        prompt.AppendLine();
        prompt.AppendLine(
            """
            How to write:
            1. Split the narration into segments in the order they are told: a hook first, an outro last, 4 to 8 segments for a few minutes.
            2. Each segment has a short title (its role and topic, e.g. "Hook - a coin that screams"), the narration as it is spoken, and factIds: the ids of every fact it uses.
            3. State nothing that is not in the facts.
            4. Use every fact marked must. Fill the rest of the time by weight: higher weights first, weight 1 only to fill time that is left.
            5. If the facts marked must alone need longer than the target, keep them all and say so in lengthNote, with how long they need. Otherwise leave lengthNote empty.
            """);
        AppendProfile(prompt, request.Instructions);
        return prompt.ToString();
    }

    internal static string SegmentPrompt(ScriptRequest request, IReadOnlyList<Segment> script, string segmentId)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Here is the script of a video in {request.Language}, segment by segment:");
        prompt.AppendLine();
        foreach (var segment in script)
        {
            prompt.AppendLine($"{segment.Id} {segment.Title} (facts {string.Join(", ", segment.FactIds)}):");
            prompt.AppendLine(segment.Narration);
            prompt.AppendLine();
        }
        var current = script.First(s => s.Id == segmentId);
        var others = script.Where(s => s.Id != segmentId).SelectMany(s => s.FactIds).ToHashSet();
        var keep = request.Facts.Facts.Where(f => !f.LeftOut && f.Weight >= Fact.MustWeight && current.FactIds.Contains(f.Id) && !others.Contains(f.Id)).Select(f => f.Id).ToList();
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"Write {segmentId} again: a new take on the same part of the video, about as long (about {ScriptSheet.Seconds(current.Narration, request.Language)} seconds), fitting between the segments before and after it.");
        if (keep.Count > 0)
        {
            prompt.AppendLine($"It must keep {string.Join(", ", keep)}: marked must, and no other segment uses them.");
        }
        prompt.AppendLine("State nothing that is not in the facts, and list in factIds every fact the new narration uses.");
        prompt.AppendLine();
        AppendFacts(prompt, request.Facts);
        AppendProfile(prompt, request.Instructions);
        return prompt.ToString();
    }

    internal static string CorrectionPrompt(IReadOnlyList<string> problems, string what)
    {
        var prompt = new StringBuilder($"Your answer has these problems:\n");
        foreach (var problem in problems)
        {
            prompt.AppendLine($"- {problem}");
        }
        prompt.AppendLine();
        prompt.AppendLine($"Fix them and hand back {what} again.");
        return prompt.ToString();
    }

    /// <summary>Only the facts in use: a fact left out is never shown to the model.</summary>
    private static void AppendFacts(StringBuilder prompt, FactSheet facts)
    {
        prompt.AppendLine("The facts you may use, and nothing else (id, weight, fact):");
        foreach (var fact in facts.Facts.Where(f => !f.LeftOut))
        {
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"{fact.Id} [{fact.Weight}{(fact.Weight >= Fact.MustWeight ? ", must" : "")}] {fact.Statement}");
        }
        prompt.AppendLine("Weights: 10 = must be in the script, 5 = normal, 1 = only if there is time left.");
    }

    private static void AppendProfile(StringBuilder prompt, string instructions)
    {
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("From the script profile:");
            prompt.AppendLine(instructions.Trim());
        }
    }

    private ClaudeCall Call(ScriptRequest request, string prompt, string schema, string? session) => new(
        // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
        Path.Combine(options.Value.DataDirectory, "script", request.ProjectId.ToString("N")),
        SystemPrompt,
        prompt,
        schema,
        request.Model,
        session,
        McpConfig: null,
        AllowedTools: [],
        "the script");

    private void Report(IProgress<ActivityLine> activity, string text) =>
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, text));

    private void ReportCost(IProgress<ActivityLine> activity, ClaudeReply reply)
    {
        if (reply.CostUsd is { } cost)
        {
            Report(activity, string.Create(CultureInfo.InvariantCulture, $"answer received · ${cost:0.00}"));
        }
    }

    private static T? Read<T>(JsonElement output) where T : class
    {
        try
        {
            return OutputSchema.Read<T>(output);
        }
        catch (JsonException)
        {
            return null;   // reported as "no script", and the model gets to try again
        }
    }
}
