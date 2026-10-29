using System.Globalization;
using StoryForge.Client;

namespace StoryForge.Engine.Assembly;

/// <summary>One shot on the narration's timeline, with the approved clip it is cut from.</summary>
/// <param name="Start">When its words start in the whole narration.</param>
/// <param name="End">When the next shot's words start; the narration's end for the last shot.</param>
/// <param name="ClipStart">Where the clip's own time 0 sits on the timeline: the shot's start minus its handle.</param>
/// <param name="ClipEnd">Where the clip runs out on the timeline.</param>
internal sealed record CutShot(
    string Key, string Label, string SegmentId, double Start, double End, double ClipStart, double ClipEnd, string ClipFile, int ClipVersion, string Description, string Heard)
{
    /// <summary>The spare clip before the shot.</summary>
    public double Head => Start - ClipStart;

    /// <summary>The spare clip after the shot.</summary>
    public double Tail => ClipEnd - End;
}

/// <summary>The narration's timeline: the voice segments one after another, and every shot on it.</summary>
/// <param name="Narration">The voice files, relative to the projects folder, in order, with their lengths.</param>
internal sealed record Timeline(IReadOnlyList<CutShot> Shots, IReadOnlyList<(string File, double Seconds)> Narration)
{
    public double Seconds => Narration.Sum(n => n.Seconds);
}

/// <summary>A piece of one clip in the render: <see cref="Frames"/> frames of its picture from its frame <see cref="FirstFrame"/>.</summary>
internal sealed record PicturePart(string File, int FirstFrame, int Frames);

/// <summary>A piece of one clip's own sound, placed at <see cref="At"/> on the timeline, faded in and out.</summary>
internal sealed record SoundPart(string File, double From, double To, double At, double FadeIn, double FadeOut);

/// <summary>
/// What the renderer makes: the pictures joined in order (a join after each but the last, its
/// overlap in frames), the narration under them, and the clips' own sound at <see cref="ClipSoundDb"/>.
/// The picture is counted in whole frames from the timeline's start, so sixty cuts do not drift off
/// the narration: the frames add up to the narration's length.
/// </summary>
internal sealed record RenderPlan(
    IReadOnlyList<PicturePart> Pictures,
    IReadOnlyList<(CutTransition Transition, int Frames)> Joins,
    IReadOnlyList<SoundPart> Sounds,
    IReadOnlyList<string> Narration,
    double Seconds,
    GenerationSize Size,
    double ClipSoundDb);

/// <summary>
/// The cut list against the clips. The narration fixes when each shot's words start; a cut moves the
/// picture change around that (Shift), a transition overlaps both clips by half its length on each
/// side, and the sound changes a little before or after the picture (J/L). All of it must stay inside
/// the clips' handles. Times are in seconds.
/// </summary>
internal static class CutPlan
{
    /// <summary>The fade on the clips' sound at a hard cut, so it does not click.</summary>
    public const double SoundFade = 0.04;

    /// <summary>How long each shot stays on screen on its own, outside its transitions.</summary>
    public const double MinShown = 0.3;

    /// <summary>Allowed rounding in the checks.</summary>
    private const double Slack = 0.001;

    /// <summary>Why the cut list does not fit the timeline, one line per problem; empty when it fits.</summary>
    public static IReadOnlyList<string> Problems(CutList list, Timeline timeline)
    {
        var shots = timeline.Shots;
        var problems = new List<string>();
        if (list.Cuts.Count != shots.Count - 1)
        {
            problems.Add($"There are {shots.Count} shots, so {shots.Count - 1} cuts: one after every shot but the last. The list has {list.Cuts.Count}.");
            return problems;
        }
        if (double.IsNaN(list.ClipSoundDb) || list.ClipSoundDb is > 0 or < CutList.MinClipSoundDb)
        {
            problems.Add(F($"The clips' sound goes from {CutList.MinClipSoundDb} dB to 0 dB, not {list.ClipSoundDb}."));
        }
        for (var j = 0; j < list.Cuts.Count; j++)
        {
            var cut = list.Cuts[j];
            var (before, after) = (shots[j], shots[j + 1]);
            var name = $"{before.Label} → {after.Label}";
            if (cut.After != before.Key)
            {
                problems.Add($"Cut {j + 1} should come after {before.Label}.");
                continue;
            }
            if (new[] { cut.Seconds, cut.Shift, cut.SoundLead }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
            {
                problems.Add($"{name}: every number must be a number.");
                continue;
            }
            if (cut.Transition == CutTransition.Cut ? cut.Seconds != 0 : cut.Seconds is < CutList.MinTransitionSeconds - Slack or > CutList.MaxTransitionSeconds + Slack)
            {
                problems.Add(cut.Transition == CutTransition.Cut
                    ? $"{name}: a hard cut has no length; give 0 s."
                    : F($"{name}: a {Name(cut.Transition)} lasts {CutList.MinTransitionSeconds} to {CutList.MaxTransitionSeconds} s, not {cut.Seconds}."));
                continue;
            }
            if (Math.Abs(cut.SoundLead) > CutList.MaxSoundLead + Slack)
            {
                problems.Add(F($"{name}: the sound changes at most {CutList.MaxSoundLead} s before or after the picture, not {Math.Abs(cut.SoundLead)} s."));
                continue;
            }
            var picture = after.Start + cut.Shift;
            var half = cut.Seconds / 2;
            if (picture + half > before.ClipEnd + Slack)
            {
                problems.Add(F($"{name}: {What(cut)} needs {picture + half - before.End:0.##} s of {before.Label}'s clip after its words; it has {before.Tail:0.##} s."));
            }
            else if (picture - half < after.ClipStart - Slack)
            {
                problems.Add(F($"{name}: {What(cut)} needs {after.Start - (picture - half):0.##} s of {after.Label}'s clip before its words; it has {after.Head:0.##} s."));
            }
            else
            {
                var sound = picture - cut.SoundLead;
                var fade = Math.Max(cut.Seconds, SoundFade) / 2;
                if (sound + fade > before.ClipEnd + Slack || sound - fade < after.ClipStart - Slack)
                {
                    problems.Add(F($"{name}: the sound changing {Lead(cut.SoundLead)} runs past the clips' handles; make it change closer to the picture."));
                }
            }
        }
        if (problems.Count == 0)
        {
            for (var i = 0; i < shots.Count; i++)
            {
                var (from, to) = Shown(list, timeline, i);
                if (to - from < MinShown - Slack)
                {
                    problems.Add(F($"{shots[i].Label} is on screen alone for only {Math.Max(0, to - from):0.##} s between its transitions; give it at least {MinShown} s."));
                }
            }
        }
        return problems;
    }

    /// <summary>The render of a cut list that fits (see <see cref="Problems"/>), at <paramref name="fps"/> frames a second.</summary>
    public static RenderPlan Render(CutList list, Timeline timeline, GenerationSize size, int fps)
    {
        var shots = timeline.Shots;
        var pictures = new List<PicturePart>();
        var sounds = new List<SoundPart>();
        var ends = new List<int>();
        int Frame(double seconds) => (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            var cutIn = i == 0 ? null : list.Cuts[i - 1];
            var cutOut = i == shots.Count - 1 ? null : list.Cuts[i];
            // The picture: from half the transition before its cut in to half the one after its cut out.
            var from = cutIn is null ? 0 : Frame(shot.Start + cutIn.Shift - cutIn.Seconds / 2);
            var to = cutOut is null ? Frame(timeline.Seconds) : Frame(shots[i + 1].Start + cutOut.Shift + cutOut.Seconds / 2);
            pictures.Add(new PicturePart(shot.ClipFile, Math.Max(0, Frame((double)from / fps - shot.ClipStart)), to - from));
            ends.Add(to);
            // Its sound: the same around where the sound changes, J/L moved, faded over the transition.
            var fadeIn = cutIn is null ? 0 : Math.Max(cutIn.Seconds, SoundFade);
            var fadeOut = cutOut is null ? 0 : Math.Max(cutOut.Seconds, SoundFade);
            var heardFrom = cutIn is null ? 0 : shot.Start + cutIn.Shift - cutIn.SoundLead - fadeIn / 2;
            var heardTo = cutOut is null ? timeline.Seconds : shots[i + 1].Start + cutOut.Shift - cutOut.SoundLead + fadeOut / 2;
            sounds.Add(new SoundPart(shot.ClipFile, Round(heardFrom - shot.ClipStart), Round(heardTo - shot.ClipStart), Round(heardFrom), fadeIn, fadeOut));
        }
        // A join overlaps the end of one picture and the start of the next: a hard cut not at all.
        var starts = pictures.Select((_, i) => i == 0 ? 0 : ends[i] - pictures[i].Frames).ToList();
        return new RenderPlan(
            pictures,
            [.. list.Cuts.Select((c, j) => (c.Transition, c.Transition == CutTransition.Cut ? 0 : ends[j] - starts[j + 1]))],
            sounds,
            [.. timeline.Narration.Select(n => n.File)],
            timeline.Seconds,
            size,
            list.ClipSoundDb);
    }

    /// <summary>When shot <paramref name="i"/> is on screen alone: between its transitions.</summary>
    private static (double From, double To) Shown(CutList list, Timeline timeline, int i)
    {
        var shots = timeline.Shots;
        var from = i == 0 ? 0 : shots[i].Start + list.Cuts[i - 1].Shift + list.Cuts[i - 1].Seconds / 2;
        var to = i == shots.Count - 1 ? timeline.Seconds : shots[i + 1].Start + list.Cuts[i].Shift - list.Cuts[i].Seconds / 2;
        return (from, to);
    }

    public static string Name(CutTransition transition) => transition switch
    {
        CutTransition.Crossfade => "crossfade",
        CutTransition.DipToBlack => "dip to black",
        _ => "cut",
    };

    /// <summary>"a 0.4 s crossfade 0.2 s later", "the cut on the words".</summary>
    private static string What(Cut cut)
    {
        var where = cut.Shift switch
        {
            > 0 => F($" {cut.Shift:0.##} s later"),
            < 0 => F($" {-cut.Shift:0.##} s earlier"),
            _ => "",
        };
        return cut.Transition == CutTransition.Cut ? $"the cut{where}" : F($"a {cut.Seconds:0.##} s {Name(cut.Transition)}{where}");
    }

    private static string Lead(double lead) => lead switch
    {
        > 0 => F($"{lead:0.##} s before the picture"),
        < 0 => F($"{-lead:0.##} s after the picture"),
        _ => "with the picture",
    };

    private static double Round(double seconds) => Math.Round(seconds, 3);

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
