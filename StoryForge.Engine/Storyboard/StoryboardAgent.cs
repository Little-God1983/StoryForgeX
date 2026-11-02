using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;

namespace StoryForge.Engine.Storyboard;

/// <summary>A segment as the storyboard sees it: its text, and its words as they are heard.</summary>
internal sealed record StoryboardSegment(Segment Segment, SegmentWords Words);

/// <summary>What the storyboard is cut from: the brief, the approved script as spoken, the storyboard profile and the pacing.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
/// <param name="Cast">
/// Re-cutting some segments only (a rerun): the cast list of the storyboard so far, whose names the
/// new shots use. Null for the whole storyboard.
/// </param>
/// <param name="Facts">The approved facts not left out: looks and shots keep to them.</param>
/// <param name="Outside">A rerun's cut: the shots it leaves in place, which its picks may name.</param>
internal sealed record StoryboardRequest(
    Guid ProjectId,
    string Brief,
    IReadOnlyList<StoryboardSegment> Segments,
    string Instructions,
    string Model,
    Pacing Pacing,
    int MaxClipSeconds,
    IReadOnlyList<CastMember>? Cast = null,
    IReadOnlyList<Fact>? Facts = null,
    StoryboardCheck.BoardOutside? Outside = null);

/// <summary>The model that plans the shots. It has no tools: the script in the prompt is all it knows.</summary>
internal interface IStoryboardAgent
{
    /// <summary>The whole storyboard; with a session and problems, the same conversation fixes its last answer.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<StoryboardOutput>> PlanAsync(
        StoryboardRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);

    /// <summary>One shot again, in the same place: what it shows, who is in it, whether it continues the one before.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<ShotRewriteOutput>> RewriteShotAsync(
        StoryboardRequest request, IReadOnlyList<Shot> board, IReadOnlyList<CastMember> cast, string shotKey, string? session,
        IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

/// <summary>The storyboard through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliStoryboardAgent(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IStoryboardAgent
{
    internal const string SystemPrompt =
        """
        You are the storyboard artist of StoryForge X, which makes narrated videos. You cut the narration
        into shots: each shot is one video clip, seen while some words of the narration are heard. You
        say what each shot shows, who and what is in it, and where it starts. You show what the
        narration is about; you never put text on screen.
        """;

    internal static string Schema { get; } = OutputSchema.Json<StoryboardOutput>();

    internal static string ShotSchema { get; } = OutputSchema.Json<ShotRewriteOutput>();

    public async Task<ScriptAnswer<StoryboardOutput>> PlanAsync(
        StoryboardRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        var what = request.Cast is null ? "the shots" : $"the shots of {string.Join(", ", request.Segments.Select(s => s.Segment.Id))}";
        Report(activity, session is null ? (request.Cast is null ? "planning the shots from the narration" : $"cutting {what} again") : "fixing the storyboard");
        var reply = await claude.AskAsync(
            Call(request, session is null ? FirstPrompt(request) : ClaudeCliScriptAgent.CorrectionPrompt(problems, request.Cast is null ? "the whole storyboard" : what), Schema, session),
            new ClaudeStream(new ResearchPages(), activity, clock, $"planning {what}"),
            cancellationToken);
        ReportCost(activity, reply);
        return new ScriptAnswer<StoryboardOutput>(reply.Session, Read<StoryboardOutput>(reply.Output));
    }

    public async Task<ScriptAnswer<ShotRewriteOutput>> RewriteShotAsync(
        StoryboardRequest request, IReadOnlyList<Shot> board, IReadOnlyList<CastMember> cast, string shotKey, string? session,
        IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        var label = ShotTiming.Label(board, shotKey);
        Report(activity, session is null ? $"planning {label} again" : $"fixing {label}");
        var reply = await claude.AskAsync(
            Call(request, session is null ? ShotPrompt(request, board, cast, shotKey) : ClaudeCliScriptAgent.CorrectionPrompt(problems, $"shot {label}"), ShotSchema, session),
            new ClaudeStream(new ResearchPages(), activity, clock, $"planning {label}"),
            cancellationToken);
        ReportCost(activity, reply);
        return new ScriptAnswer<ShotRewriteOutput>(reply.Session, Read<ShotRewriteOutput>(reply.Output));
    }

    internal static string FirstPrompt(StoryboardRequest request)
    {
        var prompt = new StringBuilder();
        if (request.Cast is null)
        {
            prompt.AppendLine("Cut the narration of this video into shots.");
        }
        else
        {
            // A rerun: these segments only. The others keep their shots, and their cast.
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"Cut these segments of the narration of this video into shots again: {string.Join(", ", request.Segments.Select(s => s.Segment.Id))}. The other segments keep their shots.");
        }
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        prompt.AppendLine();
        prompt.AppendLine("The narration, segment by segment, every word numbered with when it is heard (seconds):");
        foreach (var (segment, words) in request.Segments)
        {
            prompt.AppendLine();
            prompt.AppendLine(CultureInfo.InvariantCulture, $"{segment.Id} {segment.Title} ({ShotTiming.Seconds(words.Seconds)} s):");
            prompt.AppendLine(string.Join(' ', words.Words.Select((w, i) => string.Create(CultureInfo.InvariantCulture, $"[{i}] {w.Text} {w.Start:0.##}"))));
        }
        AppendFacts(prompt, request.Facts);
        prompt.AppendLine();
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"""
            How to cut:
            1. Every segment gets its own shots, in the order of its words. A shot never runs into the next segment.
            2. A shot starts at a word (firstWord) and ends where the next shot starts; the first shot of a segment starts at word 0.
            3. Pacing: shots of {request.Pacing.MinSeconds} to {request.Pacing.MaxSeconds} seconds, about {request.Pacing.TypicalSeconds} seconds. Cut from the story: a list of names gets quick shots, a slow explanation one long shot.
            4. Never shorter than {Pacing.ShortestShot} seconds, never longer than {request.MaxClipSeconds} seconds (the longest clip the video model makes), whatever the pacing says. A segment shorter than {Pacing.ShortestShot} seconds is one shot.
            5. Cut in a pause: after the end of a sentence first, else after a comma.
            6. For every shot say what is seen (subject, setting, light, camera), who and what is in it (at most {Shot.MaxCast}, the ones that matter most, the main subject first), and whether it continues the shot before (same scene, the camera moves on).
            7. The cast list holds everyone and everything that is shown: characters, places and objects. Only what is shown, never what is only named. Give keyReason only for something shown in just one shot that carries the story, and say why; leave it empty otherwise.
            8. Give everyone on the cast list one look, kept for the whole video: their appearance, their size against something familiar ("the size of a fist"), and the traits the facts fix. Facts come first and are kept, not bent, in the looks and in every shot: blind means no eyes are visible, not one eye. Where the facts say nothing, use what you know of the setting.
            9. In earlierStills, for everyone in a shot who is in an earlier shot too, name the earlier shot whose still best shows them for this shot, as S03-1 (segment, then the shot's number in it). Prefer the earliest shot that fits. Someone who is still visibly the same keeps their earlier still even when a detail differs: a dragon who lost the orb from his brow is still that dragon, and the shot says what changed. Leave the shot empty only when the picture cannot help, e.g. the orb is wrapped in hide there and bare here. Put the most fitting entry first: a workflow that takes one picture uses only that one.
            """);
        if (request.Cast is { } cast)
        {
            prompt.AppendLine(
                $"10. The cast of the video so far, with their looks, which stay:\n{string.Join("\n", cast.Select(Looked))}\nName them so in the shots. Your cast list needs only who and what these shots show that is not on it yet, and a look for anyone above who has none.");
        }
        if (request.Outside is { } outside)
        {
            prompt.AppendLine("11. The shots the cut leaves in place, which earlierStills may name when they come earlier:");
            foreach (var other in outside.Shots)
            {
                prompt.AppendLine($"{outside.Label(other.Key)}: {other.Description} [in it: {string.Join(", ", other.Cast)}]");
            }
            if (outside.PickAgain.Count > 0)
            {
                prompt.AppendLine("12. These later shots picked from shots you are cutting again. In laterShots, answer for each with the earlier shot that fits now:");
                foreach (var (key, lost) in outside.PickAgain)
                {
                    prompt.AppendLine($"{outside.Label(key)} must pick again for {string.Join(", ", lost)}");
                }
            }
        }
        AppendProfile(prompt, request.Instructions);
        return prompt.ToString();
    }

    internal static string ShotPrompt(StoryboardRequest request, IReadOnlyList<Shot> board, IReadOnlyList<CastMember> cast, string shotKey)
    {
        var shot = board.First(s => s.Key == shotKey);
        var words = request.Segments.First(s => s.Segment.Id == shot.SegmentId).Words.Words;
        var prompt = new StringBuilder();
        prompt.AppendLine("Here is the storyboard of a video, shot by shot:");
        prompt.AppendLine();
        foreach (var other in board.OrderBy(s => s.SegmentId, StringComparer.Ordinal).ThenBy(s => s.FirstWord))
        {
            var heard = request.Segments.First(s => s.Segment.Id == other.SegmentId).Words.Words.Skip(other.FirstWord).Take(other.WordCount).Select(w => w.Text);
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"{ShotTiming.Label(board, other.Key)} ({ShotTiming.Seconds(other.Seconds)} s, \"{string.Join(' ', heard)}\"): {other.Description}" +
                $" [in it: {string.Join(", ", other.Cast)}]{(other.ContinuesPrevious ? " (continues the shot before)" : "")}");
        }
        prompt.AppendLine();
        prompt.AppendLine("The cast list, with the looks, which stay:");
        foreach (var member in cast)
        {
            prompt.AppendLine(Looked(member));
        }
        AppendFacts(prompt, request.Facts);
        prompt.AppendLine();
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"Plan {ShotTiming.Label(board, shotKey)} again: a new take on what is seen while \"{string.Join(' ', words.Skip(shot.FirstWord).Take(shot.WordCount).Select(w => w.Text))}\" is heard. " +
            $"It keeps its place and length. Say what is seen, who and what is in it (names from the cast list, at most {Shot.MaxCast}, the main subject first), and whether it continues the shot before. " +
            $"In earlierStills, for everyone in it who is in an earlier shot too, name the earlier shot whose still best shows them for this shot, as S03-1. Someone who is still visibly the same keeps their earlier still even when a detail differs, and the shot says what changed; leave the shot empty only when the picture cannot help. Prefer the earliest that fits, and put the most fitting entry first. Keep to the facts and the looks.");
        AppendProfile(prompt, request.Instructions);
        return prompt.ToString();
    }

    /// <summary>"Karlach (character): A red tiefling."</summary>
    private static string Looked(CastMember member) =>
        $"{member.Name} ({member.Kind.ToString().ToLowerInvariant()}){(member.Look.Length > 0 ? $": {member.Look}" : "")}";

    private static void AppendFacts(StringBuilder prompt, IReadOnlyList<Fact>? facts)
    {
        if (facts is not { Count: > 0 })
        {
            return;
        }
        prompt.AppendLine();
        prompt.AppendLine("The facts of this video:");
        foreach (var fact in facts.Where(f => !f.LeftOut))
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"{fact.Id}: {fact.Statement}");
        }
    }

    private static void AppendProfile(StringBuilder prompt, string instructions)
    {
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("From the storyboard profile:");
            prompt.AppendLine(instructions.Trim());
        }
    }

    private ClaudeCall Call(StoryboardRequest request, string prompt, string schema, string? session) => new(
        // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
        Path.Combine(options.Value.DataDirectory, "storyboard", request.ProjectId.ToString("N")),
        SystemPrompt,
        prompt,
        schema,
        request.Model,
        session,
        McpConfig: null,
        AllowedTools: [],
        "the storyboard");

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
            return null;   // reported as "no storyboard", and the model gets to try again
        }
    }
}
