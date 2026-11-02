using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Storyboard;

/// <summary>A shot as the fact check reads it.</summary>
/// <param name="Label">"S02-1".</param>
internal sealed record CheckedShot(string Label, string Description);

/// <summary>What the fact check reads: the facts, and the looks and shots the shot writer just answered.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record FactCheckRequest(Guid ProjectId, string Model, IReadOnlyList<Fact> Facts, IReadOnlyList<CastMember> Cast, IReadOnlyList<CheckedShot> Shots);

/// <summary>
/// The model that reads a storyboard against the facts. Bending a fact takes judgement to catch, and
/// the shot writer thought its own answer fine, so a second reader does it. It has no tools.
/// </summary>
internal interface IStoryboardFactCheck
{
    /// <returns>What contradicts the facts; null when the answer could not be read.</returns>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<FactCheckOutput?> CheckAsync(FactCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

// What the model hands back. The schema Claude CLI enforces is generated from these records; its
// text stays plain ASCII, because npm's claude.cmd passes it through cmd.exe.

internal sealed record FactCheckOutput(
    [property: Description("Everything that contradicts a fact; empty when nothing does.")] IReadOnlyList<FactProblemOutput> Contradictions);

internal sealed record FactProblemOutput(
    [property: Description("The shot, e.g. S02-1, or the name of the cast member whose look it is.")] string Where,
    [property: Description("The fact it contradicts, e.g. F06.")] string FactId,
    [property: Description("What it says against what the fact says.")] string Problem);

/// <summary>Asks the fact check, and reads its answer.</summary>
internal static class StoryboardFacts
{
    private const int Tries = 3;

    /// <summary>The note a storyboard reaches your approval with when the check never gave a readable answer.</summary>
    public static readonly FactDisagreement Unchecked = new("", "", "The facts could not be checked.");

    /// <summary>What contradicts the facts: none with no facts to keep, <see cref="Unchecked"/> when no answer came.</summary>
    public static async Task<IReadOnlyList<FactDisagreement>> CheckAsync(
        IStoryboardFactCheck check, FactCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        if (request.Facts.Count == 0)
        {
            return [];
        }
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            FactCheckOutput? answer;
            try
            {
                answer = await check.CheckAsync(request, activity, cancellationToken);
            }
            catch (Pipeline.StageFailedException)
            {
                continue;   // the storyboard is good: it waits for you all the same, saying the facts were not checked
            }
            if (answer is not null)
            {
                return
                [
                    .. (answer.Contradictions ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Problem))
                        .Select(c => new FactDisagreement((c.Where ?? "").Trim(), (c.FactId ?? "").Trim(), c.Problem.Trim())),
                ];
            }
        }
        return [Unchecked];
    }

    /// <summary>The contradictions as the shot writer is told about them.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<FactDisagreement> found) =>
        [.. found.Where(d => d != Unchecked).Select(d => $"{d.Where} contradicts {d.FactId}: {d.Problem}")];

    /// <summary>The disagreements about a shot, kept with it: they go when it is written again.</summary>
    /// <param name="shots">With their labels as the check knew them.</param>
    public static (IReadOnlyList<Shot> Shots, IReadOnlyList<FactDisagreement> Others) Keep(IReadOnlyList<(Shot Shot, string Label)> shots, IReadOnlyList<FactDisagreement> found)
    {
        var labels = shots.Select(s => StoryboardCheck.Pick(s.Label)).ToHashSet();
        return (
            [
                .. shots.Select(s => found.Where(d => StoryboardCheck.Pick(d.Where) == StoryboardCheck.Pick(s.Label)).ToList() is { Count: > 0 } mine
                    ? s.Shot with { FactDisagreements = mine }
                    : s.Shot),
            ],
            [.. found.Where(d => !labels.Contains(StoryboardCheck.Pick(d.Where)))]);
    }
}

/// <summary>The fact check through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliStoryboardFactCheck(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IStoryboardFactCheck
{
    internal const string SystemPrompt =
        """
        You check the storyboard of a narrated video against its facts. You report only what contradicts
        a fact. What the facts do not mention is no contradiction: the storyboard may add to them.
        """;

    internal static string Schema { get; } = OutputSchema.Json<FactCheckOutput>();

    public async Task<FactCheckOutput?> CheckAsync(FactCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, "checking the storyboard against the facts"));
        var reply = await claude.AskAsync(
            new ClaudeCall(
                Path.Combine(options.Value.DataDirectory, "factcheck", request.ProjectId.ToString("N")),
                SystemPrompt,
                Prompt(request),
                Schema,
                request.Model,
                null,
                McpConfig: null,
                AllowedTools: [],
                "the fact check"),
            new ClaudeStream(new ResearchPages(), activity, clock, "checking the facts"),
            cancellationToken);
        if (reply.CostUsd is { } cost)
        {
            activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, string.Create(CultureInfo.InvariantCulture, $"answer received · ${cost:0.00}")));
        }
        try
        {
            return OutputSchema.Read<FactCheckOutput>(reply.Output);
        }
        catch (JsonException)
        {
            return null;   // asked again; in the end the storyboard reaches your approval saying so
        }
    }

    internal static string Prompt(FactCheckRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("The facts:");
        foreach (var fact in request.Facts)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"{fact.Id}: {fact.Statement}");
        }
        prompt.AppendLine();
        prompt.AppendLine("The looks of the cast:");
        foreach (var member in request.Cast)
        {
            prompt.AppendLine($"{member.Name}{(member.LookByYou ? " (given, do not raise it)" : "")}: {member.Look}");
        }
        prompt.AppendLine();
        prompt.AppendLine("The shots:");
        foreach (var shot in request.Shots)
        {
            prompt.AppendLine($"{shot.Label}: {shot.Description}");
        }
        prompt.AppendLine();
        prompt.AppendLine(
            """
            Report every look and shot that contradicts a fact, even a little: a fact says blind and a shot says one-eyed, a fact says golden and a look says red. Do not report a look marked as given, nor a shot for following one.
            """);
        return prompt.ToString();
    }
}
