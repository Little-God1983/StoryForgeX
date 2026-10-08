using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Script;

/// <summary>
/// The Script stage: segments of narration from the approved fact sheet and nothing else. Every
/// answer is checked (facts on the sheet, every must fact used, the length); a failed check goes
/// back to the model, twice, and then the stage fails with the last problem as its reason. Each
/// segment is a cell of its own, and can be written again on its own.
/// </summary>
internal sealed class ScriptStage(IScriptAgent agent, CellReader cells, ProfileStore profiles, TimeProvider clock) : ISegmentWorker
{
    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    public PipelineStage Stage => PipelineStage.Script;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var (request, factsVersion) = await RequestAsync(context, null, cancellationToken);
        var setup = context.Project.Setup;
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.WriteAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = ScriptCheck.Problems(answer.Output, request.Facts, setup.Output.Language, setup.Output.TargetSeconds);
            if (problems.Count == 0)
            {
                var script = ScriptCheck.ToSheet(answer.Output!, factsVersion);
                var seconds = script.Segments.Sum(s => ScriptSheet.Seconds(s.Narration, setup.Output.Language));
                Report(context, $"{script.Segments.Count} segments, about {ScriptCheck.Clock(seconds)} (target {ScriptCheck.Clock(setup.Output.TargetSeconds)})");
                if (script.LengthNote.Length > 0)
                {
                    Report(context, $"longer than the target: {script.LengthNote}");
                }
                var hash = Hash(setup, factsVersion);
                return new StageResult(
                    StoredJson.Write(script),
                    ScriptSheet.SchemaVersion,
                    hash,
                    [.. script.Segments.Select(s => new CellResult(s.Id, StoredJson.Write(s), ScriptSheet.SchemaVersion, hash))]);
            }
            Fail(context, attempt, problems, last: attempt == Tries || session is null);
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid script after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var written = await cells.CurrentScriptAsync(context.Project.Id, cancellationToken);
        var (request, factsVersion) = await RequestAsync(context, written?.FactsVersion, cancellationToken);
        var script = await cells.CurrentSegmentsAsync(context.Project.Id, cancellationToken);
        if (script.All(s => s.Id != key))
        {
            throw new StageFailedException($"The script has no segment {key}.");
        }
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.RewriteAsync(request, script, key, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = ScriptCheck.SegmentProblems(answer.Output, key, request.Facts, script);
            if (problems.Count == 0)
            {
                var segment = ScriptCheck.ToSegment(answer.Output!, key);
                Report(context, $"{key} rewritten, about {ScriptCheck.Clock(ScriptSheet.Seconds(segment.Narration, request.Language))}");
                return new CellResult(key, StoredJson.Write(segment), ScriptSheet.SchemaVersion, Hash(context.Project.Setup, factsVersion));
            }
            Fail(context, attempt, problems, last: attempt == Tries || session is null);
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid segment after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>Everything a script is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, int factsVersion) => InputHash.Of(new
    {
        stage = PipelineStage.Script,
        schema = ScriptSheet.SchemaVersion,
        facts = factsVersion,
        brief = setup.Brief,
        provider = setup.Writing.Provider,
        model = setup.Writing.Model,
        profile = setup.Writing.Script,
        language = setup.Output.Language,
        target = setup.Output.TargetSeconds,
    });

    /// <summary>
    /// What the model is asked from: the fact sheet of <paramref name="factsVersion"/> (a segment keeps
    /// to the sheet its script was written from), else the approved one.
    /// </summary>
    private async Task<(ScriptRequest Request, int FactsVersion)> RequestAsync(StageContext context, int? factsVersion, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Writing.Provider != ResearchStage.ClaudeCli)
        {
            throw new StageFailedException(
                $"The script is written through Claude CLI for now, and this project uses {setup.Writing.Provider}. LM Studio follows with #19.");
        }
        (FactSheet Sheet, int Version) facts;
        if (factsVersion is { } wanted && await cells.FactSheetAsync(context.Project.Id, wanted, cancellationToken) is { } written)
        {
            facts = (written, wanted);
        }
        else
        {
            facts = await cells.ApprovedFactSheetAsync(context.Project.Id, cancellationToken)
                ?? throw new StageFailedException("The fact sheet is not approved yet. Approve it, and the script is written from it.");
        }
        if (facts.Sheet.Facts.All(f => f.LeftOut))
        {
            throw new StageFailedException("Every fact on the approved fact sheet is left out, so there is nothing to write from.");
        }
        var profile = await profiles.GetVersionAsync(setup.Writing.Script.ProfileId, setup.Writing.Script.Version, cancellationToken);
        return (new ScriptRequest(
            context.Project.Id,
            setup.Brief,
            facts.Sheet,
            profile.Content.Instructions,
            setup.Writing.Model,
            setup.Output.Language,
            setup.Output.TargetSeconds), facts.Version);
    }

    private void Fail(StageContext context, int attempt, IReadOnlyList<string> problems, bool last)
    {
        var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
        Report(context, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
    }

    private void Report(StageContext context, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Check, text));
}
