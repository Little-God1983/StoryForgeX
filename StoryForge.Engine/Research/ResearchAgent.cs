using StoryForge.Client;

namespace StoryForge.Engine.Research;

/// <summary>What the research is asked: the project's brief and sources, and the research profile's instructions.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record ResearchRequest(
    Guid ProjectId,
    string Brief,
    IReadOnlyList<string> Sources,
    string Instructions,
    string Model,
    string Language,
    int TargetSeconds);

/// <summary>One answer of the research model, and what it read to get there.</summary>
/// <param name="Session">Where a correction continues the same conversation; null if it cannot.</param>
/// <param name="Output">The fact sheet as the model wrote it; null if it wrote none.</param>
/// <param name="Pages">Every page read so far in this research, this answer's and the earlier ones'.</param>
internal sealed record ResearchAnswer(string? Session, ResearchOutput? Output, ResearchPages Pages);

/// <summary>The model that does the research, with the research tools and nothing else.</summary>
internal interface IResearchAgent
{
    /// <summary>
    /// Asks for a fact sheet. With <paramref name="previous"/> and <paramref name="problems"/>, asks
    /// the same conversation to fix its last answer.
    /// </summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked, or gave up.</exception>
    Task<ResearchAnswer> AskAsync(
        ResearchRequest request,
        ResearchAnswer? previous,
        IReadOnlyList<string> problems,
        IProgress<ActivityLine> activity,
        CancellationToken cancellationToken);
}
