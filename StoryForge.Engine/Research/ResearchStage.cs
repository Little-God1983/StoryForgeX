using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;

namespace StoryForge.Engine.Research;

/// <summary>
/// The Research stage: a fact sheet from the project's sources only. Every answer is checked; a
/// failed check goes back to the model with the problems, twice, and then the stage fails with the
/// last problem as its reason.
/// </summary>
internal sealed class ResearchStage(IResearchAgent agent, ProfileStore profiles, TimeProvider clock) : IStageWorker
{
    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    public const string ClaudeCli = "Claude CLI";

    public PipelineStage Stage => PipelineStage.Research;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Writing.Provider != ClaudeCli)
        {
            throw new StageFailedException(
                $"Research runs through Claude CLI for now, and this project uses {setup.Writing.Provider}. LM Studio follows with #19.");
        }
        if (setup.ResearchSources.Count == 0)
        {
            throw new StageFailedException("The project has no research sources, so there is nothing research may read.");
        }

        var profile = await profiles.GetVersionAsync(setup.Writing.Research.ProfileId, setup.Writing.Research.Version, cancellationToken);
        var request = new ResearchRequest(
            context.Project.Id,
            setup.Brief,
            setup.ResearchSources,
            profile.Content.Instructions,
            setup.Writing.Model,
            setup.Output.Language,
            setup.Output.TargetSeconds);

        ResearchAnswer? answer = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            answer = await agent.AskAsync(request, answer, problems, context.Activity, cancellationToken);
            problems = FactSheetCheck.Problems(answer.Output, answer.Pages);
            if (problems.Count == 0)
            {
                var sheet = FactSheetCheck.ToSheet(answer.Output!);
                Report(context, $"{sheet.Facts.Count} facts from {answer.Pages.Count} pages");
                return new StageResult(StoredJson.Write(sheet), FactSheet.SchemaVersion, Hash(setup));
            }
            var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
            var last = attempt == Tries || answer.Session is null;   // without a session, nothing to send the problems back to
            Report(context, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
            if (last)
            {
                break;
            }
        }
        throw new StageFailedException(
            $"The answer was not a valid fact sheet after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>Everything a fact sheet is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup) => InputHash.Of(new
    {
        stage = PipelineStage.Research,
        schema = FactSheet.SchemaVersion,
        brief = setup.Brief,
        sources = setup.ResearchSources,
        provider = setup.Writing.Provider,
        model = setup.Writing.Model,
        profile = setup.Writing.Research,
        language = setup.Output.Language,
        target = setup.Output.TargetSeconds,
    });

    private void Report(StageContext context, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Check, text));
}
