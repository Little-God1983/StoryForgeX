using StoryForge.Client;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Research;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Assembly;

/// <summary>Your cut list waiting to be rendered, and the version you edited it from.</summary>
internal sealed record WaitingCutList(CutList CutList, int? BasedOn);

/// <summary>Builds the narration's timeline from the storyboard, the voices it was cut to, and the approved clips.</summary>
internal sealed class AssemblyTimeline(CellReader cells)
{
    /// <summary>The shots on the timeline; a shot without an approved clip is left out and named in <c>Missing</c>.</summary>
    public async Task<(Timeline Timeline, IReadOnlyList<string> Missing, IReadOnlyDictionary<string, int> Voices)> BuildAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var shots = await cells.CurrentShotsAsync(projectId, cancellationToken);
        var board = await cells.CurrentStoryboardAsync(projectId, cancellationToken);
        var clips = await cells.ApprovedClipsAsync(projectId, cancellationToken);
        var narration = new List<(string File, double Seconds)>();
        var voices = new Dictionary<string, int>();
        var placed = new List<(Shot Shot, double Start, string Heard)>();
        var offset = 0.0;
        foreach (var segment in shots.Select(s => s.SegmentId).Distinct())
        {
            var version = board?.VoiceVersions.GetValueOrDefault(segment);
            var voice = version is { } v ? await cells.VoiceAsync(projectId, segment, v, cancellationToken) : null;
            if (voice is null)
            {
                throw new StageFailedException($"{segment} has no voice the storyboard was cut to. Speak it and cut the storyboard first.");
            }
            voices[segment] = version!.Value;
            narration.Add((voice.AudioFile, voice.Seconds));
            foreach (var shot in shots.Where(s => s.SegmentId == segment))
            {
                placed.Add((shot, offset + shot.Start, string.Join(' ', voice.Words.Skip(shot.FirstWord).Take(shot.WordCount).Select(w => w.Text))));
            }
            offset += voice.Seconds;
        }
        var missing = new List<string>();
        var timeline = new List<CutShot>();
        for (var i = 0; i < placed.Count; i++)
        {
            var (shot, start, heard) = placed[i];
            var label = ShotTiming.Label(shots, shot.Key);
            if (!clips.TryGetValue(shot.Key, out var clip))
            {
                missing.Add(label);
                continue;
            }
            // A shot runs until the next one's words start (a pause after a segment's last word is its too).
            var end = i + 1 < placed.Count ? placed[i + 1].Start : offset;
            var clipStart = start - clip.Clip.Handle;
            timeline.Add(new CutShot(
                shot.Key, label, shot.SegmentId, Math.Round(start, 3), Math.Round(end, 3), Math.Round(clipStart, 3), Math.Round(clipStart + clip.Clip.Seconds, 3),
                clip.Clip.File, clip.Version, shot.Description, heard));
        }
        return (new Timeline(timeline, narration), missing, voices);
    }
}

/// <summary>
/// The Assembly stage: Claude writes the cut list from the storyboard, the words and the timings
/// (it cannot watch the clips), the engine checks it against the clips' handles, and FFmpeg renders
/// the finished video at the delivery size with the narration and the clips' own sound under it. Your
/// cut list (Render again) is rendered as it is, without Claude. The clip files are never changed.
/// </summary>
internal sealed class AssemblyStage(
    ICutListAgent agent,
    IVideoRenderer renderer,
    AssemblyTimeline timelines,
    CellReader cells,
    ProjectFolders folders,
    TimeProvider clock) : IStageWorker
{
    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    public PipelineStage Stage => PipelineStage.Assembly;

    public string CancelledAfter(int stored) => "Cancelled. Regenerate cuts the video again.";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        if (await cells.StateAsync(project.Id, PipelineStage.Clips, cancellationToken) != StageState.Approved)
        {
            throw new StageFailedException("Approve every clip first: the video is cut from them.");
        }
        var (timeline, missing, voices) = await timelines.BuildAsync(project.Id, cancellationToken);
        if (missing.Count > 0)
        {
            throw new StageFailedException($"{string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no approved clip. Approve the clips first.");
        }
        if (timeline.Shots.Count == 0)
        {
            throw new StageFailedException("The storyboard has no shots yet.");
        }
        var root = await folders.RootAsync(cancellationToken);
        if (timeline.Shots.FirstOrDefault(s => !File.Exists(Path.Combine(root, s.ClipFile))) is { } gone)
        {
            throw new StageFailedException($"{gone.Label}: its approved clip is gone from the projects folder. Make the clip again first.");
        }

        var waiting = await cells.WaitingCutListAsync(project.Id, cancellationToken);
        CutList list;
        if (waiting is not null)
        {
            if (CutPlan.Problems(waiting.CutList, timeline) is [var first, ..])
            {
                throw new StageFailedException($"Your cut list no longer fits the clips: {first} Regenerate asks Claude for a new one.");
            }
            list = waiting.CutList;
            Report(context, ActivityKind.Check, "your cut list, rendered as it is");
        }
        else
        {
            var sound = (await cells.CurrentAssemblyAsync(project.Id, cancellationToken))?.Video.CutList.ClipSoundDb ?? CutList.DefaultClipSoundDb;
            list = await CutListAsync(context, timeline, sound, cancellationToken);
        }

        var size = new GenerationSize(project.Setup.Output.Aspect, project.Setup.Output.Width, project.Setup.Output.Height);
        var plan = CutPlan.Render(list, timeline, size, ClipsStage.FramesPerSecond);
        var file = Path.Combine(ProjectFolders.Of(project), "assembly", $"video-{Guid.NewGuid().ToString("N")[..8]}.mp4");
        await renderer.RenderAsync(plan, root, Path.Combine(root, file), ClipsStage.FramesPerSecond,
            new Progress(line => Report(context, ActivityKind.Model, line)), cancellationToken);
        Report(context, ActivityKind.Check, $"video rendered · {ShotTiming.Seconds(timeline.Seconds)} s · {size.Width}×{size.Height}");

        var clips = timeline.Shots.ToDictionary(s => s.Key, s => s.ClipVersion);
        var video = new AssembledVideo(list, file, Math.Round(timeline.Seconds, 3), size, clips, voices, waiting?.BasedOn);
        return new StageResult(StoredJson.Write(video), AssembledVideo.SchemaVersion, Hash(project.Setup, clips, voices, list));
    }

    /// <summary>Everything the video is cut from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, IReadOnlyDictionary<string, int> clips, IReadOnlyDictionary<string, int> voices, CutList list) => InputHash.Of(new
    {
        stage = PipelineStage.Assembly,
        schema = AssembledVideo.SchemaVersion,
        clips = clips.OrderBy(c => c.Key, StringComparer.Ordinal).ToList(),
        voices = voices.OrderBy(v => v.Key, StringComparer.Ordinal).ToList(),
        size = new { setup.Output.Width, setup.Output.Height },
        cuts = list,
    });

    /// <summary>Claude's cut list; a failed check goes back to it, twice.</summary>
    private async Task<CutList> CutListAsync(StageContext context, Timeline timeline, double clipSoundDb, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Script.Provider != ResearchStage.ClaudeCli)
        {
            throw new StageFailedException(
                $"The cut list is written through Claude CLI for now, and this project uses {setup.Script.Provider}. LM Studio follows with #19.");
        }
        if (timeline.Shots.Count == 1)
        {
            return new CutList([], clipSoundDb, []);   // one shot: nothing to cut
        }
        var request = new CutListRequest(context.Project.Id, setup.Brief, timeline, setup.Script.Model);
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.WriteAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            (var list, problems) = CutListCheck.Read(answer.Output, timeline, clipSoundDb);
            if (list is not null)
            {
                Report(context, ActivityKind.Check, $"{list.Cuts.Count} cuts, {list.Cuts.Count(c => c.Transition != CutTransition.Cut)} of them transitions");
                return list;
            }
            var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
            var last = attempt == Tries || session is null;
            Report(context, ActivityKind.Check, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException($"The answer had no good cut list after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    private void Report(StageContext context, ActivityKind kind, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>Reports at once on the caller's thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
