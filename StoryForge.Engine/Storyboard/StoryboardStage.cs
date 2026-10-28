using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Storyboard;

/// <summary>
/// The Storyboard stage: every approved segment cut into shots by its word timings, each shot one
/// clip. Every answer is checked (every segment covered in order, the length limits, at most four
/// in a shot, everyone on the cast list); a failed check goes back to the model, twice, and then
/// the stage fails with the last problem as its reason. Each shot is a cell of its own, and can be
/// planned again on its own; its place and length stay.
/// </summary>
internal sealed class StoryboardStage(IStoryboardAgent agent, CellReader cells, ProfileStore profiles, TimeProvider clock) : ISegmentWorker
{
    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    public PipelineStage Stage => PipelineStage.Storyboard;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        if (context.Scope is { } scope)
        {
            return await RecutAsync(context, scope, cancellationToken);
        }
        var (request, voices) = await RequestAsync(context, null, cancellationToken);
        var segments = request.Segments.Select(s => s.Words).ToList();
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.PlanAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = StoryboardCheck.Problems(answer.Output, segments, request.MaxClipSeconds);
            if (problems.Count == 0)
            {
                var shots = StoryboardCheck.ToShots(answer.Output!, segments, await cells.UsedShotKeysAsync(context.Project.Id, cancellationToken));
                var sheet = new StoryboardSheet(StoryboardCheck.ToCast(answer.Output!), voices, request.Pacing);
                Report(context, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{shots.Count} shots · shortest {ShotTiming.Seconds(shots.Min(s => s.Seconds))} s · longest {ShotTiming.Seconds(shots.Max(s => s.Seconds))} s · {sheet.Cast.Count} in the cast"));
                var hash = Hash(context.Project.Setup, voices, request.Pacing);
                return new StageResult(
                    StoredJson.Write(sheet),
                    Shot.SchemaVersion,
                    hash,
                    [.. shots.Select(s => new CellResult(s.Key, StoredJson.Write(s), Shot.SchemaVersion, hash))]);
            }
            Fail(context, attempt, problems, last: attempt == Tries || session is null);
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid storyboard after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>
    /// A rerun: the shots of these segments cut again from their approved voices. The other segments
    /// keep their shots, and the voices they were cut from; the cast list keeps everyone on it and
    /// takes in whoever the new shots show besides. The result's segments are the new shots only.
    /// </summary>
    private async Task<StageResult> RecutAsync(StageContext context, IReadOnlySet<string> scope, CancellationToken cancellationToken)
    {
        var sheet = await cells.CurrentStoryboardAsync(context.Project.Id, cancellationToken)
            ?? throw new StageFailedException("The storyboard has not been planned yet.");
        var (all, voices) = await RequestAsync(context, null, cancellationToken);
        var request = all with { Segments = [.. all.Segments.Where(s => scope.Contains(s.Segment.Id))], Cast = sheet.Cast };
        if (request.Segments.Count == 0)
        {
            throw new StageFailedException($"The script has no segment {string.Join(", ", scope)} to cut again.");
        }
        var segments = request.Segments.Select(s => s.Words).ToList();
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.PlanAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            // The cast so far counts as on the list: the answer's own list need name only who is new.
            var output = answer.Output is null ? null : answer.Output with
            {
                Cast = [.. sheet.Cast.Select(c => new CastOutput(c.Name, c.Kind.ToString().ToLowerInvariant(), c.KeyReason)), .. answer.Output.Cast ?? []],
            };
            problems = StoryboardCheck.Problems(output, segments, request.MaxClipSeconds);
            if (problems.Count == 0)
            {
                // Everyone on the list stays, as they are spelled there; the new shots name them so.
                var cast = StoryboardCheck.ToCast(output!);
                var names = StoryboardCheck.Names(cast.Select(c => c.Name));
                var shots = StoryboardCheck.ToShots(output!, segments, await cells.UsedShotKeysAsync(context.Project.Id, cancellationToken))
                    .Select(s => s with { Cast = StoryboardCheck.CastOf(s.Cast, names) })
                    .ToList();
                var cutFrom = sheet.VoiceVersions.ToDictionary();
                foreach (var id in scope.Where(voices.ContainsKey))
                {
                    cutFrom[id] = voices[id];
                }
                Report(context, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{string.Join(", ", scope.Order(StringComparer.Ordinal))} cut again: {shots.Count} shots · shortest {ShotTiming.Seconds(shots.Min(s => s.Seconds))} s · longest {ShotTiming.Seconds(shots.Max(s => s.Seconds))} s"));
                var hash = Hash(context.Project.Setup, cutFrom, request.Pacing);
                return new StageResult(
                    StoredJson.Write(new StoryboardSheet(cast, cutFrom, request.Pacing)),
                    Shot.SchemaVersion,
                    hash,
                    [.. shots.Select(s => new CellResult(s.Key, StoredJson.Write(s), Shot.SchemaVersion, hash))]);
            }
            Fail(context, attempt, problems, last: attempt == Tries || session is null);
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid storyboard after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var sheet = await cells.CurrentStoryboardAsync(context.Project.Id, cancellationToken)
            ?? throw new StageFailedException("The storyboard has not been planned yet.");
        var board = await cells.CurrentShotsAsync(context.Project.Id, cancellationToken);
        var shot = board.FirstOrDefault(s => s.Key == key) ?? throw new StageFailedException($"The storyboard has no shot {key}.");
        // The words the board was cut from, not a voice spoken since: the shot keeps its place in them.
        var (request, _) = await RequestAsync(context, sheet.VoiceVersions, cancellationToken);
        var label = ShotTiming.Label(board, key);
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.RewriteShotAsync(request, board, sheet.Cast, key, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = StoryboardCheck.RewriteProblems(answer.Output, label, sheet.Cast);
            if (problems.Count == 0)
            {
                var names = StoryboardCheck.Names(sheet.Cast.Select(c => c.Name));
                var again = shot with
                {
                    Description = answer.Output!.Description.Trim(),
                    Cast = StoryboardCheck.CastOf(answer.Output.Cast, names),
                    ContinuesPrevious = answer.Output.ContinuesPrevious,
                };
                Report(context, $"{label} planned again");
                return new CellResult(key, StoredJson.Write(again), Shot.SchemaVersion, Hash(context.Project.Setup, sheet.VoiceVersions, sheet.Pacing));
            }
            Fail(context, attempt, problems, last: attempt == Tries || session is null);
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid shot after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>Everything a storyboard is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, IReadOnlyDictionary<string, int> voices, Pacing pacing) => InputHash.Of(new
    {
        stage = PipelineStage.Storyboard,
        schema = Shot.SchemaVersion,
        voices = voices.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(),
        provider = setup.Script.Provider,
        model = setup.Script.Model,
        profile = setup.Script.Storyboard,
        pacing,
        maxClip = setup.Clips.MaxClipSeconds,
    });

    /// <summary>
    /// What the model is asked from: the approved segments and their voices (or with
    /// <paramref name="voiceVersions"/>, those versions of the voices), the profile and the pacing.
    /// </summary>
    private async Task<(StoryboardRequest Request, IReadOnlyDictionary<string, int> Voices)> RequestAsync(
        StageContext context, IReadOnlyDictionary<string, int>? voiceVersions, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Script.Provider != ResearchStage.ClaudeCli)
        {
            throw new StageFailedException(
                $"The storyboard is planned through Claude CLI for now, and this project uses {setup.Script.Provider}. LM Studio follows with #19.");
        }
        var script = await cells.ApprovedSegmentsAsync(context.Project.Id, cancellationToken);
        if (script.Count == 0)
        {
            throw new StageFailedException("The script has no segments yet. Approve the script and its voice, and the storyboard is planned.");
        }
        var approved = (await cells.ApprovedVoicesAsync(context.Project.Id, cancellationToken)).ToDictionary(v => v.Clip.SegmentId);
        var segments = new List<StoryboardSegment>();
        var voices = new Dictionary<string, int>();
        foreach (var (segment, _) in script)
        {
            VoiceClip? clip = null;
            int version = 0;
            if (voiceVersions is not null && voiceVersions.TryGetValue(segment.Id, out var wanted))
            {
                clip = await cells.VoiceAsync(context.Project.Id, segment.Id, wanted, cancellationToken);
                version = wanted;
            }
            else if (approved.TryGetValue(segment.Id, out var found))
            {
                (clip, version) = found;
            }
            if (clip is null || clip.Words.Count == 0)
            {
                throw new StageFailedException($"{segment.Id} has no voice yet. Speak it, and the storyboard is planned from its word timings.");
            }
            segments.Add(new StoryboardSegment(segment, new SegmentWords(segment.Id, clip.Words, clip.Seconds)));
            voices[segment.Id] = version;
        }
        var profile = await profiles.GetVersionAsync(setup.Script.Storyboard.ProfileId, setup.Script.Storyboard.Version, cancellationToken);
        return (new StoryboardRequest(
            context.Project.Id,
            setup.Brief,
            segments,
            profile.Content.Instructions,
            setup.Script.Model,
            (setup.Pacing ?? profile.Content.Pacing ?? Pacing.Default).HeldTo(setup.Clips.MaxClipSeconds),
            setup.Clips.MaxClipSeconds), voices);
    }

    private void Fail(StageContext context, int attempt, IReadOnlyList<string> problems, bool last)
    {
        var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
        Report(context, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
    }

    private void Report(StageContext context, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Check, text));
}
