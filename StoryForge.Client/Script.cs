namespace StoryForge.Client;

/// <summary>One segment of the script: a row of the result matrix, e.g. "S01 Hook – a coin that screams".</summary>
/// <param name="Id">"S01", unique within the script.</param>
/// <param name="FactIds">The facts of the approved fact sheet the narration uses, e.g. ["F01", "F07"].</param>
public sealed record Segment(string Id, string Title, string Narration, IReadOnlyList<string> FactIds);

/// <summary>The Script stage's result: its segments, and a word on the length when it cannot meet the target.</summary>
/// <param name="LengthNote">Why the script is longer than the target, e.g. "The facts marked must need about 5:10."; empty when it fits.</param>
public sealed record ScriptSheet(IReadOnlyList<Segment> Segments, string LengthNote)
{
    /// <summary>Raised whenever the stored shape changes, so older scripts can still be read.</summary>
    public const int SchemaVersion = 1;

    /// <summary>How many words of narration a minute holds in a language; the script is measured by it.</summary>
    public static int WordsPerMinute(string language) => language.Trim().ToLowerInvariant() switch
    {
        "english" => 150,
        "german" or "deutsch" => 130,
        _ => 140,
    };

    /// <summary>How long a narration takes to speak, in whole seconds.</summary>
    public static int Seconds(string narration, string language)
    {
        var words = narration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return (int)Math.Round(words * 60.0 / WordsPerMinute(language));
    }
}

/// <summary>One segment as the matrix shows it: its current version, its state and its versions.</summary>
/// <param name="Version">The version shown and used; switching to an older one makes it current again.</param>
/// <param name="Error">Why the last regeneration of this segment failed; null unless the state is Failed.</param>
/// <param name="Seconds">How long the narration takes to speak.</param>
public sealed record SegmentView(
    string Id,
    string Title,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    string Narration,
    IReadOnlyList<string> FactIds,
    int Seconds,
    string? Error);

/// <summary>Everything the result matrix shows of the Script stage.</summary>
/// <param name="Error">Why the last run of the whole script failed; null unless the state is Failed.</param>
/// <param name="Seconds">How long all segments together take to speak.</param>
/// <param name="Facts">The approved fact sheet the script was written from, to show what a segment uses.</param>
public sealed record ScriptView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    IReadOnlyList<SegmentView> Segments,
    string LengthNote,
    int Seconds,
    int TargetSeconds,
    IReadOnlyList<Fact> Facts);
