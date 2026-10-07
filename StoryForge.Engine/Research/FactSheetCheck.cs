using StoryForge.Client;

namespace StoryForge.Engine.Research;

/// <summary>
/// Checks the model's answer before it becomes a fact sheet. Each problem is written so the model
/// can fix it when it is sent back, and so you can read it on the failed stage.
/// </summary>
internal static class FactSheetCheck
{
    public static IReadOnlyList<string> Problems(ResearchOutput? output, ResearchPages pages)
    {
        if (output is null)
        {
            return ["The answer had no fact sheet."];
        }
        if (output.Facts is not { Count: > 0 })
        {
            return [pages.Count == 0
                ? "The fact sheet has no facts, and no page was read. Search the sources and fetch pages first."
                : "The fact sheet has no facts."];
        }

        var problems = new List<string>();
        for (var i = 0; i < output.Facts.Count; i++)
        {
            var fact = output.Facts[i];
            var id = Id(i);
            if (fact is null)
            {
                problems.Add($"{id} is empty.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(fact.Statement))
            {
                problems.Add($"{id} has no statement.");
            }
            if (string.IsNullOrWhiteSpace(fact.SourceUrl))
            {
                problems.Add($"{id} has no source link.");
                continue;
            }
            if (!pages.WasRead(fact.SourceUrl))
            {
                problems.Add($"{id} points to {fact.SourceUrl}, which was not read in this research. Fetch the page, or use a page you read.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(fact.Quote))
            {
                problems.Add($"{id} has no quote.");
            }
            else if (!pages.Contains(fact.SourceUrl, fact.Quote))
            {
                problems.Add($"{id}: the quote is not on {fact.SourceUrl} as it was read. Copy the passage word for word.");
            }
        }
        return problems;
    }

    /// <summary>The fact sheet from a checked answer: numbered F01, F02, …, every weight at the default.</summary>
    public static FactSheet ToSheet(ResearchOutput output) =>
        new([.. output.Facts.Select((f, i) => new Fact(Id(i), f.Statement.Trim(), f.SourceUrl.Trim(), f.Quote.Trim()))]);

    private static string Id(int index) => $"F{index + 1:00}";
}
