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
    [property: Description("Empty, unless it is shown in only one shot but carries the story: then say why.")] string KeyReason,
    [property: Description("What it looks like for the whole video: appearance, size against something familiar, the traits the facts fix.")] string Look);

internal sealed record SegmentShotsOutput(
    [property: Description("The segment id, e.g. S01.")] string SegmentId,
    [property: Description("The segment's shots in the order of the narration.")] IReadOnlyList<ShotOutput> Shots);

internal sealed record ShotOutput(
    [property: Description("The number of the word the shot starts at. The first shot starts at 0; the shot ends where the next starts.")] int FirstWord,
    [property: Description("What is seen: subject, setting, light, camera.")] string Description,
    [property: Description("Who and what is in it, names from the cast list, at most 4.")] IReadOnlyList<string> Cast,
    [property: Description("True when it is the same scene as the shot before, the camera moving on.")] bool ContinuesPrevious,
    [property: Description("One entry for each one in it who is in an earlier shot too, most fitting first.")] IReadOnlyList<EarlierStillOutput> EarlierStills);

internal sealed record EarlierStillOutput(
    [property: Description("Who, a name from the cast list.")] string Cast,
    [property: Description("The earlier shot whose still shows them best for this shot, e.g. S03-1. Empty when no earlier still fits.")] string Shot);

internal sealed record ShotRewriteOutput(
    [property: Description("What is seen: subject, setting, light, camera.")] string Description,
    [property: Description("Who and what is in it, names from the cast list, at most 4.")] IReadOnlyList<string> Cast,
    [property: Description("True when it is the same scene as the shot before, the camera moving on.")] bool ContinuesPrevious,
    [property: Description("One entry for each one in it who is in an earlier shot too, most fitting first.")] IReadOnlyList<EarlierStillOutput> EarlierStills);

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
        foreach (var same in cast.Where(c => !string.IsNullOrWhiteSpace(c?.Name)).GroupBy(c => Normal(c.Name)))
        {
            if (same.All(c => string.IsNullOrWhiteSpace(c.Look)))
            {
                problems.Add($"{same.First().Name.Trim()} has no look on the cast list.");
            }
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
        problems.AddRange(PickProblems(AllShots(output, segments), names));
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
    /// <param name="used">Keys that shots of this storyboard have versions under: a new shot takes none of them, nor their history.</param>
    public static IReadOnlyList<Shot> ToShots(StoryboardOutput output, IReadOnlyList<SegmentWords> segments, IEnumerable<string>? used = null, Func<string>? random = null)
    {
        var names = Names(output.Cast ?? []);
        var shots = new List<Shot>();
        var keys = new HashSet<string>(used ?? []);
        var labels = new Dictionary<string, string>();
        var picks = new List<IReadOnlyList<EarlierStillOutput>?>();
        foreach (var segment in segments)
        {
            // Empty entries skipped, as the check skips them.
            var answered = output.Segments.First(a => a?.SegmentId?.Trim() == segment.SegmentId).Shots.Where(s => s is not null).ToList();
            for (var i = 0; i < answered.Count; i++)
            {
                var first = answered[i].FirstWord;
                var count = (i + 1 < answered.Count ? answered[i + 1].FirstWord : segment.Words.Count) - first;
                var (start, end) = ShotTiming.Span(segment, first, count);
                shots.Add(new Shot(NewKey(segment.SegmentId, keys, random), segment.SegmentId, first, count, start, end,
                    answered[i].Description.Trim(), CastOf(answered[i].Cast, names), answered[i].ContinuesPrevious));
                labels[Pick($"{segment.SegmentId}-{i + 1}")] = shots[^1].Key;
                picks.Add(answered[i].EarlierStills);
            }
        }
        return [.. shots.Select((shot, i) => shot with { EarlierStills = ToPicks(picks[i], names, labels) })];
    }

    /// <summary>The picks of an answer, as the cast list spells them and with shot keys; "no earlier still fits" is no entry.</summary>
    /// <param name="labels">The shot key of each label, in the form of <see cref="Pick"/>.</param>
    public static IReadOnlyList<EarlierStill> ToPicks(IEnumerable<EarlierStillOutput>? picks, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> labels) =>
        [
            .. (picks ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Shot) && labels.ContainsKey(Pick(p.Shot)))
                .Select(p => new EarlierStill(names.TryGetValue(Normal(p.Cast), out var name) ? name : (p.Cast ?? "").Trim(), labels[Pick(p.Shot)])),
        ];

    /// <summary>"s03-1" for " S03·1": how a shot named in a pick is compared.</summary>
    public static string Pick(string shot) => shot.Trim().Replace('·', '-').ToLowerInvariant();

    /// <summary>The cast list, everyone once: names that differ only in case or spaces are one, as first written.</summary>
    public static IReadOnlyList<CastMember> ToCast(StoryboardOutput output) =>
        [
            .. (output.Cast ?? []).Where(c => !string.IsNullOrWhiteSpace(c?.Name)).GroupBy(c => Normal(c.Name)).Select(same => new CastMember(
                same.First().Name.Trim(),
                ToKind(same.First().Kind) ?? CastKind.Object,
                same.Select(c => (c.KeyReason ?? "").Trim()).FirstOrDefault(r => r.Length > 0) ?? "",
                same.Select(c => (c.Look ?? "").Trim()).FirstOrDefault(l => l.Length > 0) ?? "")),
        ];

    /// <summary>Who and what a shot names, as the cast list spells them.</summary>
    public static IReadOnlyList<string> CastOf(IEnumerable<string>? named, IReadOnlyDictionary<string, string> names) =>
        [.. (named ?? []).Select(n => names.TryGetValue(Normal(n), out var name) ? name : (n ?? "").Trim()).Where(n => n.Length > 0).Distinct()];

    /// <summary>
    /// A new shot's key: its segment and four characters of its own, e.g. "S01/3f9a", that no shot
    /// in <paramref name="taken"/> has; it is added there.
    /// </summary>
    /// <param name="random">Four characters at a time; tests pass their own.</param>
    public static string NewKey(string segmentId, ISet<string> taken, Func<string>? random = null)
    {
        random ??= () => Guid.NewGuid().ToString("N")[..4];
        string key;
        do
        {
            key = $"{segmentId}/{random()}";
        }
        while (!taken.Add(key));
        return key;
    }

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

    /// <summary>A shot as the checks on picks see it.</summary>
    /// <param name="Label">"S02·1".</param>
    private sealed record PickedShot(string Label, IReadOnlyList<string>? Cast, IReadOnlyList<EarlierStillOutput>? Picks);

    /// <summary>Every shot of an answer, in the order of the segments.</summary>
    private static List<PickedShot> AllShots(StoryboardOutput output, IReadOnlyList<SegmentWords> segments) =>
        [
            .. segments.SelectMany(segment => ((output.Segments ?? []).FirstOrDefault(a => a?.SegmentId?.Trim() == segment.SegmentId)?.Shots ?? [])
                .Where(s => s is not null)
                .Select((shot, i) => new PickedShot($"{segment.SegmentId}·{i + 1}", shot.Cast, shot.EarlierStills))),
        ];

    /// <summary>
    /// Everyone seen in an earlier shot needs an entry: empty is a decision, silence goes back. A
    /// picked shot is an earlier one with them in it.
    /// </summary>
    private static IEnumerable<string> PickProblems(IReadOnlyList<PickedShot> board, IReadOnlyDictionary<string, string> names)
    {
        var earlier = new Dictionary<string, HashSet<string>>();   // label as Pick has it -> who is in it
        foreach (var shot in board)
        {
            var shown = CastOf(shot.Cast, names);
            var inShot = shown.Select(Normal).ToHashSet();
            var answered = new HashSet<string>();
            foreach (var pick in (shot.Picks ?? []).Where(p => p is not null))
            {
                var who = Normal(pick.Cast);
                var name = names.GetValueOrDefault(who, (pick.Cast ?? "").Trim());
                if (!inShot.Contains(who))
                {
                    yield return $"{shot.Label} picks an earlier still for {name}, who is not in it.";
                    continue;
                }
                if (!answered.Add(who))
                {
                    yield return $"{shot.Label} picks twice for {name}; give one.";
                    continue;
                }
                if (string.IsNullOrWhiteSpace(pick.Shot))
                {
                    continue;
                }
                if (!earlier.TryGetValue(Pick(pick.Shot), out var there))
                {
                    yield return $"{shot.Label} picks the still of {pick.Shot.Trim()} for {name}, which is not an earlier shot.";
                }
                else if (!there.Contains(who))
                {
                    yield return $"{shot.Label} picks the still of {pick.Shot.Trim()} for {name}, who is not in that shot.";
                }
            }
            foreach (var name in shown.Where(n => !answered.Contains(Normal(n))))
            {
                var seen = earlier.FirstOrDefault(e => e.Value.Contains(Normal(name)));
                if (seen.Key is not null)
                {
                    yield return $"{shot.Label} shows {name}, who is in {seen.Key.ToUpperInvariant()} too: name the earlier shot whose still fits, or leave it empty when none does.";
                }
            }
            earlier[Pick(shot.Label)] = inShot;
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

    /// <summary>"house of hope" for " House of  Hope": what tells two names apart.</summary>
    private static string Normal(string? name) => string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>One of the three words, any case; a number or a mix ("character, place") is none of them.</summary>
    private static CastKind? ToKind(string? kind) =>
        Enum.GetValues<CastKind>().Select(k => (CastKind?)k).FirstOrDefault(k => string.Equals(k.ToString(), (kind ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
}
