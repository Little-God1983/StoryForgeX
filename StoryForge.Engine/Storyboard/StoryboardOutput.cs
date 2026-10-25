using System.ComponentModel;
using StoryForge.Client;

namespace StoryForge.Engine.Storyboard;

// What the model hands back. The engine times the shots from the word timings and gives them their
// keys. The JSON schema Claude CLI enforces is generated from these records; its text stays plain
// ASCII, because npm's claude.cmd passes it through cmd.exe.

internal sealed record StoryboardOutput(
    [property: Description("Everyone and everything the shots show: characters, places and objects. Never what is only named.")] IReadOnlyList<CastOutput> Cast,
    [property: Description("Every segment of the script, in order, with its shots.")] IReadOnlyList<SegmentShotsOutput> Segments);

internal sealed record CastOutput(
    [property: Description("The name, e.g. Karlach, the House of Hope, a soul coin.")] string Name,
    [property: Description("character, place or object.")] string Kind,
    [property: Description("Empty, unless it is shown in only one shot but carries the story: then say why.")] string KeyReason);

internal sealed record SegmentShotsOutput(
    [property: Description("The segment id, e.g. S01.")] string SegmentId,
    [property: Description("The segment's shots in the order of the narration.")] IReadOnlyList<ShotOutput> Shots);

internal sealed record ShotOutput(
    [property: Description("The number of the word the shot starts at. The first shot starts at 0; the shot ends where the next starts.")] int FirstWord,
    [property: Description("What is seen: subject, setting, light, camera.")] string Description,
    [property: Description("Who and what is in it, names from the cast list, at most 4.")] IReadOnlyList<string> Cast,
    [property: Description("True when it is the same scene as the shot before, the camera moving on.")] bool ContinuesPrevious);

internal sealed record ShotRewriteOutput(
    [property: Description("What is seen: subject, setting, light, camera.")] string Description,
    [property: Description("Who and what is in it, names from the cast list, at most 4.")] IReadOnlyList<string> Cast,
    [property: Description("True when it is the same scene as the shot before, the camera moving on.")] bool ContinuesPrevious);

/// <summary>Checks the model's storyboard before it becomes the Storyboard stage's result.</summary>
internal static class StoryboardCheck
{
    public static IReadOnlyList<string> Problems(StoryboardOutput? output, IReadOnlyList<SegmentWords> segments, int maxClipSeconds)
    {
        if (output is null)
        {
            return ["The answer had no storyboard."];
        }
        var problems = new List<string>();
        var cast = output.Cast ?? [];
        foreach (var member in cast.Where(c => c is not null && ToKind(c.Kind) is null))
        {
            problems.Add($"The cast list says {member.Name} is a \"{member.Kind}\"; it must be character, place or object.");
        }
        var names = Names(cast);
        var answered = (output.Segments ?? []).Where(s => s is not null).ToList();
        foreach (var extra in answered.Where(a => segments.All(s => s.SegmentId != a.SegmentId?.Trim())))
        {
            problems.Add($"{extra.SegmentId} is not a segment of the script.");
        }
        foreach (var segment in segments)
        {
            var shots = answered.FirstOrDefault(a => a.SegmentId?.Trim() == segment.SegmentId)?.Shots?.Where(s => s is not null).ToList();
            if (shots is not { Count: > 0 })
            {
                problems.Add($"{segment.SegmentId} has no shots.");
                continue;
            }
            problems.AddRange(SegmentProblems(segment, shots, names, maxClipSeconds));
        }
        return problems;
    }

    /// <summary>Checks one shot written again; its timing stays as it was.</summary>
    /// <param name="label">"S01·2".</param>
    public static IReadOnlyList<string> RewriteProblems(ShotRewriteOutput? output, string label, IReadOnlyList<CastMember> cast)
    {
        if (output is null)
        {
            return ["The answer had no shot."];
        }
        return [.. ContentProblems(label, output.Description, output.Cast, Names(cast.Select(c => c.Name)))];
    }

    /// <summary>The shots from a checked answer: timed from the word timings, each with a key of its own.</summary>
    public static IReadOnlyList<Shot> ToShots(StoryboardOutput output, IReadOnlyList<SegmentWords> segments)
    {
        var names = Names(output.Cast ?? []);
        var shots = new List<Shot>();
        foreach (var segment in segments)
        {
            var answered = output.Segments.First(a => a.SegmentId.Trim() == segment.SegmentId).Shots;
            for (var i = 0; i < answered.Count; i++)
            {
                var first = answered[i].FirstWord;
                var count = (i + 1 < answered.Count ? answered[i + 1].FirstWord : segment.Words.Count) - first;
                var (start, end) = ShotTiming.Span(segment, first, count);
                shots.Add(new Shot(NewKey(segment.SegmentId), segment.SegmentId, first, count, start, end,
                    answered[i].Description.Trim(), CastOf(answered[i].Cast, names), answered[i].ContinuesPrevious));
            }
        }
        return shots;
    }

    public static IReadOnlyList<CastMember> ToCast(StoryboardOutput output) =>
        [.. (output.Cast ?? []).Select(c => new CastMember(c.Name.Trim(), ToKind(c.Kind) ?? CastKind.Object, (c.KeyReason ?? "").Trim()))];

    /// <summary>Who and what a shot names, as the cast list spells them.</summary>
    public static IReadOnlyList<string> CastOf(IEnumerable<string>? named, IReadOnlyDictionary<string, string> names) =>
        [.. (named ?? []).Select(n => names.TryGetValue(Normal(n), out var name) ? name : (n ?? "").Trim()).Where(n => n.Length > 0).Distinct()];

    /// <summary>A new shot's key: its segment and four characters of its own, e.g. "S01/3f9a".</summary>
    public static string NewKey(string segmentId) => $"{segmentId}/{Guid.NewGuid():N}"[..(segmentId.Length + 5)];

    private static IEnumerable<string> SegmentProblems(SegmentWords segment, List<ShotOutput> shots, IReadOnlyDictionary<string, string> names, int maxClipSeconds)
    {
        var last = segment.Words.Count - 1;
        for (var i = 0; i < shots.Count; i++)
        {
            var label = $"{segment.SegmentId}·{i + 1}";
            var first = shots[i].FirstWord;
            if (i == 0 && first != 0)
            {
                yield return $"{label} must start at word 0, not word {first}.";
            }
            else if (i > 0 && first <= shots[i - 1].FirstWord)
            {
                yield return $"{label} starts at word {first}, which is not after {segment.SegmentId}·{i} (word {shots[i - 1].FirstWord}). Shots go in the order of the narration.";
            }
            else if (first > last)
            {
                yield return $"{label} starts at word {first}, but {segment.SegmentId} has {segment.Words.Count} words (0 to {last}).";
            }
            foreach (var problem in ContentProblems(label, shots[i].Description, shots[i].Cast, names))
            {
                yield return problem;
            }
        }
        // Lengths only once the order is right: out of order they mean nothing.
        var ordered = shots[0].FirstWord == 0 && shots.Zip(shots.Skip(1)).All(p => p.First.FirstWord < p.Second.FirstWord) && shots[^1].FirstWord <= last;
        if (!ordered)
        {
            yield break;
        }
        for (var i = 0; i < shots.Count; i++)
        {
            var next = i + 1 < shots.Count ? shots[i + 1].FirstWord : segment.Words.Count;
            var (start, end) = ShotTiming.Span(segment, shots[i].FirstWord, next - shots[i].FirstWord);
            if (ShotTiming.LengthProblem($"{segment.SegmentId}·{i + 1}", end - start, segment, maxClipSeconds) is { } problem)
            {
                yield return problem;
            }
        }
    }

    private static IEnumerable<string> ContentProblems(string label, string? description, IReadOnlyList<string>? cast, IReadOnlyDictionary<string, string> names)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            yield return $"{label} has no description.";
        }
        var shown = CastOf(cast, names);
        if (shown.Count > Shot.MaxCast)
        {
            yield return $"{label} has {shown.Count} in it; a clip takes at most {Shot.MaxCast}. Keep the {Shot.MaxCast} that matter most and describe the rest.";
        }
        foreach (var name in shown.Where(n => !names.ContainsKey(Normal(n))))
        {
            yield return $"{label} has {name} in it, who is not on the cast list.";
        }
    }

    private static Dictionary<string, string> Names(IEnumerable<CastOutput> cast) => Names(cast.Select(c => c?.Name));

    /// <summary>The cast list's names by their normal form: "karlach" → "Karlach".</summary>
    public static Dictionary<string, string> Names(IEnumerable<string?> cast)
    {
        var names = new Dictionary<string, string>();
        foreach (var name in cast.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            names.TryAdd(Normal(name), name!.Trim());
        }
        return names;
    }

    private static string Normal(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static CastKind? ToKind(string? kind) =>
        Enum.TryParse<CastKind>((kind ?? "").Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
