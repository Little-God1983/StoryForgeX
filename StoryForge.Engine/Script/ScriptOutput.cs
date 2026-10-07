using System.ComponentModel;
using System.Globalization;
using StoryForge.Client;

namespace StoryForge.Engine.Script;

// What the model hands back. The engine numbers the segments (S01, S02, ...). The JSON schema
// Claude CLI enforces is generated from these records; its text stays plain ASCII, because npm's
// claude.cmd passes it through cmd.exe.

internal sealed record ScriptOutput(
    [property: Description("The segments in the order they are told: a hook first, an outro last.")] IReadOnlyList<ScriptPart> Segments,
    [property: Description("Empty when the script fits the target length. If the facts marked must alone need longer, say so here, with how long they need.")] string LengthNote);

internal sealed record ScriptPart(
    [property: Description("A short title: the segment's role and topic, e.g. Hook - a coin that screams.")] string Title,
    [property: Description("The narration as it is spoken, in the language the prompt names.")] string Narration,
    [property: Description("The ids of every fact the narration uses, e.g. F01. Only ids from the fact list.")] IReadOnlyList<string> FactIds);

/// <summary>Checks the model's script before it becomes the Script stage's result.</summary>
internal static class ScriptCheck
{
    /// <summary>Longer than the target by more than this needs a length note.</summary>
    public const double TooLong = 1.2;

    /// <summary>Shorter than the target by more than this, with facts left unused, goes back.</summary>
    public const double TooShort = 0.6;

    public static IReadOnlyList<string> Problems(ScriptOutput? output, FactSheet sheet, string language, int targetSeconds)
    {
        if (output is null)
        {
            return ["The answer had no script."];
        }
        if (output.Segments is not { Count: > 0 })
        {
            return ["The script has no segments."];
        }

        var problems = new List<string>();
        for (var i = 0; i < output.Segments.Count; i++)
        {
            problems.AddRange(PartProblems(output.Segments[i], Id(i), sheet));
        }
        var used = output.Segments.Where(s => s?.FactIds is not null).SelectMany(s => s.FactIds).ToHashSet();
        foreach (var must in sheet.Facts.Where(f => !f.LeftOut && f.Weight >= Fact.MustWeight && !used.Contains(f.Id)))
        {
            problems.Add($"{must.Id} is marked must, but no segment uses it.");
        }

        var seconds = output.Segments.Where(s => s?.Narration is not null).Sum(s => ScriptSheet.Seconds(s.Narration, language));
        if (seconds > targetSeconds * TooLong && string.IsNullOrWhiteSpace(output.LengthNote))
        {
            problems.Add(
                $"The script runs about {Clock(seconds)}; the target is {Clock(targetSeconds)}. Shorten it, dropping the lowest weights first. " +
                "If the facts marked must alone need longer, keep them all and say so in lengthNote.");
        }
        var unused = sheet.Facts.Count(f => !f.LeftOut && !used.Contains(f.Id));
        if (seconds < targetSeconds * TooShort && unused > 0)
        {
            problems.Add($"The script runs about {Clock(seconds)}; the target is {Clock(targetSeconds)}. Use more of the {unused} unused facts, highest weights first.");
        }
        return problems;
    }

    /// <summary>
    /// Checks one rewritten segment. The facts marked must that only this segment carried must still
    /// be in it: the rest of the script does not change.
    /// </summary>
    public static IReadOnlyList<string> SegmentProblems(ScriptPart? part, string id, FactSheet sheet, IReadOnlyList<Segment> others)
    {
        var problems = PartProblems(part, id, sheet).ToList();
        if (part?.FactIds is null)
        {
            return problems;
        }
        var elsewhere = others.Where(s => s.Id != id).SelectMany(s => s.FactIds).ToHashSet();
        foreach (var must in sheet.Facts.Where(f => !f.LeftOut && f.Weight >= Fact.MustWeight && !elsewhere.Contains(f.Id) && !part.FactIds.Contains(f.Id)))
        {
            problems.Add($"{must.Id} is marked must and no other segment uses it, so {id} has to keep it.");
        }
        return problems;
    }

    /// <summary>The script from a checked answer: segments numbered S01, S02, … in order.</summary>
    public static ScriptSheet ToSheet(ScriptOutput output) =>
        new([.. output.Segments.Select((s, i) => ToSegment(s, Id(i)))], output.LengthNote?.Trim() ?? "");

    public static Segment ToSegment(ScriptPart part, string id) =>
        new(id, part.Title.Trim(), part.Narration.Trim(), [.. part.FactIds.Select(f => f.Trim()).Distinct()]);

    public static string Id(int index) => $"S{index + 1:00}";

    /// <summary>"3:52".</summary>
    public static string Clock(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    private static IEnumerable<string> PartProblems(ScriptPart? part, string id, FactSheet sheet)
    {
        if (part is null)
        {
            yield return $"{id} is empty.";
            yield break;
        }
        if (string.IsNullOrWhiteSpace(part.Title))
        {
            yield return $"{id} has no title.";
        }
        if (string.IsNullOrWhiteSpace(part.Narration))
        {
            yield return $"{id} has no narration.";
        }
        if (part.FactIds is not { Count: > 0 })
        {
            yield return $"{id} names no facts. Every segment says which facts it uses.";
            yield break;
        }
        foreach (var factId in part.FactIds.Select(f => (f ?? "").Trim()).Distinct())
        {
            var fact = sheet.Facts.FirstOrDefault(f => f.Id == factId);
            if (fact is null)
            {
                yield return $"{id} uses {factId}, which is not on the fact sheet.";
            }
            else if (fact.LeftOut)
            {
                yield return $"{id} uses {factId}, which was left out. Use only the facts in the list.";
            }
        }
    }
}
