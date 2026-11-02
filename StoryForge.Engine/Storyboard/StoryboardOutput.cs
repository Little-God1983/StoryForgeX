using System.ComponentModel;
using StoryForge.Client;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Storyboard;

// What the model hands back. The engine times the shots from the word timings and gives them their
// keys. The JSON schema Claude CLI enforces is generated from these records; its text stays plain
// ASCII, because npm's claude.cmd passes it through cmd.exe.

internal sealed record StoryboardOutput(
    [property: Description("Everyone and everything the shots show: characters, places and objects. Never what is only named.")] IReadOnlyList<CastOutput> Cast,
    [property: Description("Every segment of the script, in order, with its shots.")] IReadOnlyList<SegmentShotsOutput> Segments,
    [property: Description("Only when asked: later shots that must pick again, with their new picks.")] IReadOnlyList<LaterShotOutput>? LaterShots = null);

internal sealed record LaterShotOutput(
    [property: Description("The later shot, e.g. S03-2.")] string Shot,
    [property: Description("One entry for each one it must pick again for.")] IReadOnlyList<EarlierStillOutput> EarlierStills);

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
    /// <param name="outside">A rerun's cut: the shots it leaves in place, and which of them must pick again.</param>
    public static IReadOnlyList<string> Problems(StoryboardOutput? output, IReadOnlyList<SegmentWords> segments, int maxClipSeconds, BoardOutside? outside = null)
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
        problems.AddRange(PickProblems(AllShots(output, segments, outside), names));
        foreach (var extra in (output.LaterShots ?? []).Where(l => l is not null && outside?.PickAgain.Keys.Any(k => Pick(outside.Label(k)) == Pick(l.Shot ?? "")) != true))
        {
            problems.Add($"{extra.Shot} was not asked for: answer only for the later shots that must pick again.");
        }
        return problems;
    }

    /// <summary>Checks one shot written again; its timing stays as it was.</summary>
    /// <param name="label">"S01·2".</param>
    /// <param name="board">The storyboard the shot is on, for its picks.</param>
    public static IReadOnlyList<string> RewriteProblems(ShotRewriteOutput? output, string label, IReadOnlyList<CastMember> cast, IReadOnlyList<Shot>? board = null)
    {
        if (output is null)
        {
            return ["The answer had no shot."];
        }
        var names = Names(cast.Select(c => c.Name));
        IEnumerable<string> problems = ContentProblems(label, output.Description, output.Cast, names);
        if (board is not null)
        {
            var before = Ordered(board).Select(s => new PickedShot(ShotTiming.Label(board, s.Key), s.Cast, null, new HashSet<string>())).TakeWhile(s => s.Label != label);
            problems = problems.Concat(PickProblems([.. before, new PickedShot(label, output.Cast, output.EarlierStills)], names));
        }
        return [.. problems];
    }

    /// <summary>The shots in the order they are seen: by segment, then by where they start.</summary>
    private static IOrderedEnumerable<Shot> Ordered(IEnumerable<Shot> board) => board.OrderBy(s => s.SegmentId, StringComparer.Ordinal).ThenBy(s => s.FirstWord);

    /// <summary>
    /// The shots a rerun's cut leaves in place, and what the cut takes from them: a shot that picked
    /// from a shot now going away must pick again.
    /// </summary>
    /// <param name="Board">The whole storyboard before the cut, for the labels.</param>
    /// <param name="Shots">The shots staying.</param>
    /// <param name="PickAgain">Of those, by key: who lost their pick, as the cast list spells them.</param>
    internal sealed record BoardOutside(IReadOnlyList<Shot> Board, IReadOnlyList<Shot> Shots, IReadOnlyDictionary<string, IReadOnlyList<string>> PickAgain)
    {
        public string Label(string key) => StillPromptCheck.Name(ShotTiming.Label(Board, key));

        public static BoardOutside Of(IReadOnlyList<Shot> board, IReadOnlySet<string> segments)
        {
            var going = board.Where(s => segments.Contains(s.SegmentId)).Select(s => s.Key).ToHashSet();
            var staying = board.Where(s => !going.Contains(s.Key)).ToList();
            return new(board, staying, staying
                .Where(s => s.EarlierStills?.Any(p => going.Contains(p.ShotKey)) == true)
                .ToDictionary(s => s.Key, s => (IReadOnlyList<string>)[.. s.EarlierStills!.Where(p => going.Contains(p.ShotKey)).Select(p => p.Cast)]));
        }
    }

    /// <summary>The shots that picked again, with the answer's picks in place of the ones lost.</summary>
    public static IReadOnlyList<Shot> PickedAgain(StoryboardOutput output, BoardOutside outside, IReadOnlyList<Shot> cut)
    {
        var names = Names(output.Cast ?? []);
        var labels = Labels(outside, cut);
        return
        [
            .. outside.Shots.Where(s => outside.PickAgain.ContainsKey(s.Key)).Select(shot =>
            {
                var lost = outside.PickAgain[shot.Key];
                var answer = (output.LaterShots ?? []).FirstOrDefault(l => l is not null && Pick(l.Shot ?? "") == Pick(outside.Label(shot.Key)));
                return shot with
                {
                    EarlierStills = [.. shot.EarlierStills!.Where(p => !lost.Contains(p.Cast)), .. ToPicks(answer?.EarlierStills, names, labels)],
                };
            }),
        ];
    }

    /// <summary>Every shot of the storyboard after the cut, by label as <see cref="Pick"/> has it.</summary>
    public static IReadOnlyDictionary<string, string> Labels(BoardOutside outside, IReadOnlyList<Shot> cut)
    {
        var labels = outside.Shots.ToDictionary(s => Pick(outside.Label(s.Key)), s => s.Key);
        foreach (var shot in cut)
        {
            labels[Pick(ShotTiming.Label(cut, shot.Key))] = shot.Key;
        }
        return labels;
    }

    /// <summary>The shots from a checked answer: timed from the word timings, each with a key of its own.</summary>
    /// <param name="used">Keys that shots of this storyboard have versions under: a new shot takes none of them, nor their history.</param>
    /// <param name="outside">A rerun's cut: the shots it leaves in place, which its picks may name.</param>
    public static IReadOnlyList<Shot> ToShots(StoryboardOutput output, IReadOnlyList<SegmentWords> segments, IEnumerable<string>? used = null, Func<string>? random = null, BoardOutside? outside = null)
    {
        var names = Names(output.Cast ?? []);
        var shots = new List<Shot>();
        var keys = new HashSet<string>(used ?? []);
        var labels = outside is null ? [] : outside.Shots.ToDictionary(s => Pick(outside.Label(s.Key)), s => s.Key);
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
    /// <param name="Asked">Who must answer, by normal name; null for everyone seen before. A shot not answered for asks no one.</param>
    private sealed record PickedShot(string Label, IReadOnlyList<string>? Cast, IReadOnlyList<EarlierStillOutput>? Picks, IReadOnlySet<string>? Asked = null);

    /// <summary>Every shot of the storyboard after an answer, in the order they are seen.</summary>
    private static List<PickedShot> AllShots(StoryboardOutput output, IReadOnlyList<SegmentWords> segments, BoardOutside? outside)
    {
        var answered = segments.SelectMany(segment => ((output.Segments ?? []).FirstOrDefault(a => a?.SegmentId?.Trim() == segment.SegmentId)?.Shots ?? [])
            .Where(s => s is not null)
            .Select((shot, i) => (segment.SegmentId, Word: shot.FirstWord, Shot: new PickedShot($"{segment.SegmentId}·{i + 1}", shot.Cast, shot.EarlierStills))));
        if (outside is null)
        {
            return [.. answered.Select(a => a.Shot)];
        }
        return
        [
            .. answered.Concat(outside.Shots.Select(s => (s.SegmentId, Word: s.FirstWord, Shot: Staying(s))))
                .OrderBy(a => a.SegmentId, StringComparer.Ordinal).ThenBy(a => a.Word).Select(a => a.Shot),
        ];

        PickedShot Staying(Shot shot)
        {
            var label = ShotTiming.Label(outside.Board, shot.Key);
            return outside.PickAgain.TryGetValue(shot.Key, out var lost)
                ? new PickedShot(label, shot.Cast, (output.LaterShots ?? []).FirstOrDefault(l => l is not null && Pick(l.Shot ?? "") == Pick(label))?.EarlierStills, lost.Select(Normal).ToHashSet())
                : new PickedShot(label, shot.Cast, null, new HashSet<string>());
        }
    }

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
            foreach (var name in shown.Where(n => !answered.Contains(Normal(n)) && (shot.Asked?.Contains(Normal(n)) ?? true)))
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
