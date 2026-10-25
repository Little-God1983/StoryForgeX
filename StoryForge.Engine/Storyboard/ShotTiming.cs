using StoryForge.Client;

namespace StoryForge.Engine.Storyboard;

/// <summary>A segment's spoken words with their timings, and how long its audio is: what shots are cut from.</summary>
internal sealed record SegmentWords(string SegmentId, IReadOnlyList<SpokenWord> Words, double Seconds);

/// <summary>
/// Where shots start and end. A cut falls between two words, in the middle of the pause between
/// them; the first shot of a segment starts with its audio and the last ends with it, so a
/// segment's shots always add up to its audio.
/// </summary>
internal static class ShotTiming
{
    /// <summary>When the cut before word <paramref name="index"/> falls; 0 for the first word.</summary>
    public static double CutAt(IReadOnlyList<SpokenWord> words, int index) =>
        index <= 0 ? 0 : (words[index - 1].End + words[index].Start) / 2;

    /// <summary>When a shot of <paramref name="count"/> words from <paramref name="first"/> starts and ends.</summary>
    public static (double Start, double End) Span(SegmentWords segment, int first, int count) =>
        (CutAt(segment.Words, first), first + count >= segment.Words.Count ? segment.Seconds : CutAt(segment.Words, first + count));

    /// <summary>Why a shot of this length cannot be; null when it can.</summary>
    /// <param name="label">"S01·2".</param>
    /// <param name="proposed">A change you asked for ("would last"), not the model's answer ("lasts … Cut it.").</param>
    public static string? LengthProblem(string label, double seconds, SegmentWords segment, int maxClipSeconds, bool proposed = false)
    {
        var lasts = proposed ? "would last" : "lasts";
        // A segment shorter than that is one shot: the exception is for the whole of it, never a part.
        var wholeSegment = seconds >= segment.Seconds - 0.001;
        if (seconds < Pacing.ShortestShot && !wholeSegment)
        {
            return $"{label} {lasts} {Seconds(seconds)} s; a shot is at least {Pacing.ShortestShot} s.";
        }
        return seconds > maxClipSeconds
            ? $"{label} {lasts} {Seconds(seconds)} s; the video model makes at most {maxClipSeconds} s.{(proposed ? "" : " Cut it.")}"
            : null;
    }

    /// <summary>A shot as the screen names it, by its place in its segment: "S01·2".</summary>
    public static string Label(IEnumerable<Shot> board, string key)
    {
        var shot = board.First(s => s.Key == key);
        return $"{shot.SegmentId}·{1 + board.Count(s => s.SegmentId == shot.SegmentId && s.FirstWord < shot.FirstWord)}";
    }

    /// <summary>"1.45", "25".</summary>
    public static string Seconds(double seconds) => Math.Round(seconds, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
