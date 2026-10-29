using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Assembly;

/// <summary>What the cut list is written from: the brief and the shots on the narration's timeline.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record CutListRequest(Guid ProjectId, string Brief, Timeline Timeline, string Model);

/// <summary>The model that writes the cut list. It cannot watch the clips: it cuts from the storyboard, the words and the timings.</summary>
internal interface ICutListAgent
{
    /// <summary>One cut after every shot but the last; with a session and problems, the same conversation fixes its last answer.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<CutListOutput>> WriteAsync(
        CutListRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

// What the model hands back. The schema Claude CLI enforces is generated from these records (and
// Claude reads its descriptions); its text stays plain ASCII, because npm's claude.cmd passes it
// through cmd.exe.

internal sealed record CutListOutput(
    [property: Description("One cut after every shot but the last, in shot order.")] IReadOnlyList<CutOutput> Cuts);

internal sealed record CutOutput(
    [property: Description("The shot the cut ends, as the prompt names it, e.g. S01-2.")] string After,
    [property: Description("cut, crossfade or dip (a dip to black).")] string Transition,
    [property: Description("How long a crossfade or dip lasts, 0.2 to 1.0; 0 for a cut.")] double Seconds,
    [property: Description("Where the picture changes against the next shot's words: -0.2 is 0.2 s before them, 0 on them.")] double Shift,
    [property: Description("When the clips' own sound changes against the picture: 0.3 is 0.3 s before it (J cut), -0.3 after it (L cut), 0 with it.")] double SoundLead,
    [property: Description("Why, in a few words.")] string Why);

/// <summary>The cut list through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliCutListAgent(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : ICutListAgent
{
    internal const string SystemPrompt =
        """
        You are the editor of StoryForge X, which makes narrated videos. Every shot of the storyboard
        is one video clip, timed to a fixed narration. You decide how each shot gives way to the next:
        a hard cut, a crossfade or a dip to black, exactly where the picture changes, and whether the
        clips' own sound changes a little before or after the picture. You cannot watch the clips; you
        cut from what each shot shows and what is heard meanwhile.
        """;

    internal static string Schema { get; } = OutputSchema.Json<CutListOutput>();

    public async Task<ScriptAnswer<CutListOutput>> WriteAsync(
        CutListRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        Report(activity, session is null ? "writing the cut list" : "fixing the cut list");
        var reply = await claude.AskAsync(
            new ClaudeCall(
                // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
                Path.Combine(options.Value.DataDirectory, "assembly", request.ProjectId.ToString("N")),
                SystemPrompt,
                session is null ? FirstPrompt(request) : ClaudeCliScriptAgent.CorrectionPrompt(problems, "every cut"),
                Schema,
                request.Model,
                session,
                McpConfig: null,
                AllowedTools: [],
                "the cut list"),
            new ClaudeStream(new ResearchPages(), activity, clock, "writing the cut list"),
            cancellationToken);
        if (reply.CostUsd is { } cost)
        {
            Report(activity, string.Create(CultureInfo.InvariantCulture, $"answer received · ${cost:0.00}"));
        }
        CutListOutput? output;
        try
        {
            output = OutputSchema.Read<CutListOutput>(reply.Output);
        }
        catch (JsonException)
        {
            output = null;   // reported as "no cuts", and the model gets to try again
        }
        return new ScriptAnswer<CutListOutput>(reply.Session, output);
    }

    internal static string FirstPrompt(CutListRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Write the cut list for this video: one cut after every shot but the last.");
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        prompt.AppendLine();
        prompt.AppendLine("The shots in order, with when their words start in the narration and how much clip each has to spare before and after:");
        var shots = request.Timeline.Shots;
        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            if (i > 0 && shots[i - 1].SegmentId != shot.SegmentId)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"-- a new segment of the script starts here ({shot.SegmentId}) --");
            }
            prompt.AppendLine(CultureInfo.InvariantCulture,
                $"{StillPromptCheck.Name(shot.Label)} at {shot.Start:0.##} s, {shot.End - shot.Start:0.##} s long, spare {shot.Head:0.##} s before / {shot.Tail:0.##} s after: {shot.Description}");
            if (shot.Heard.Length > 0)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"  heard: \"{shot.Heard}\"");
            }
        }
        prompt.AppendLine();
        prompt.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"""
            How to cut:
            1. The order of the shots and the narration are fixed. You only decide how each shot gives way to the next.
            2. Mostly hard cuts (transition cut, seconds 0), on or near the next shot's first word. A cut a little before the words (shift -0.1 to -0.3) often feels natural.
            3. A crossfade ({CutList.MinTransitionSeconds} to {CutList.MaxTransitionSeconds} s) where time passes or the place changes softly; a dip to black only at the end of a part of the story, rarely.
            4. A transition overlaps both clips by half its length on each side, and the shift moves it: half its length plus the shift must fit in the spare clip after the shot, and half its length minus the shift in the spare clip before the next one.
            5. The clips' own sound (ambience, under the voice) may change a little before the picture (sound lead 0.2 to 0.5, a J cut) when the next place should be heard first, or after it (negative, an L cut) when a sound rings on. It must fit in the spare clip too. Mostly 0.
            6. Every shot stays on screen on its own for at least {CutPlan.MinShown} s outside its transitions.
            """));
        return prompt.ToString();
    }

    private void Report(IProgress<ActivityLine> activity, string text) =>
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, text));
}

/// <summary>Claude's answer as a cut list, or why it is not one yet.</summary>
internal static class CutListCheck
{
    public static (CutList? List, IReadOnlyList<string> Problems) Read(CutListOutput? output, Timeline timeline, double clipSoundDb)
    {
        if (output?.Cuts is not { } answered || answered.Any(c => c is null))
        {
            return (null, ["The answer had no cuts."]);
        }
        var shots = timeline.Shots;
        var problems = new List<string>();
        var cuts = new List<Cut>();
        for (var j = 0; j < answered.Count; j++)
        {
            var cut = answered[j];
            var shot = shots.FirstOrDefault(s => string.Equals(StillPromptCheck.Name(s.Label), cut.After?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (shot is null)
            {
                problems.Add($"Cut {j + 1} is after '{cut.After}', which is not one of the shots.");
                continue;
            }
            if (Transition(cut.Transition) is not { } transition)
            {
                problems.Add($"{StillPromptCheck.Name(shot.Label)}: the transition is cut, crossfade or dip, not '{cut.Transition}'.");
                continue;
            }
            cuts.Add(new Cut(shot.Key, transition, transition == CutTransition.Cut ? 0 : Math.Round(cut.Seconds, 2), Math.Round(cut.Shift, 2), Math.Round(cut.SoundLead, 2), cut.Why?.Trim() ?? ""));
        }
        if (problems.Count > 0)
        {
            return (null, problems);
        }
        var list = new CutList(cuts, clipSoundDb, []);
        var fit = CutPlan.Problems(list, timeline);
        // The model names shots as S01-2; the labels it reads back are S01·2.
        return fit.Count == 0 ? (list, []) : (null, [.. fit.Select(p => p.Replace('·', '-'))]);
    }

    private static CutTransition? Transition(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "cut" or "hard cut" => CutTransition.Cut,
        "crossfade" or "cross fade" or "dissolve" or "fade" => CutTransition.Crossfade,
        "dip" or "dip to black" or "fade to black" => CutTransition.DipToBlack,
        _ => null,
    };
}
