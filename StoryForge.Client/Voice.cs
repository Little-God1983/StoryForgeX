namespace StoryForge.Client;

/// <summary>One spoken word and when it is heard in the segment's audio, e.g. "coins" from 0.71 s to 1.02 s.</summary>
/// <remarks>Estimated from the text and the measured length of each part, not heard: see <c>WordTimings</c>.</remarks>
public sealed record SpokenWord(string Text, double Start, double End);

/// <summary>One ComfyUI run of a segment: at most about 45 s of narration, as longer audio goes bad.</summary>
/// <param name="Seconds">The measured length of the audio this part gave.</param>
public sealed record VoicePart(string Text, double Seconds);

/// <summary>A segment's narration spoken: one audio file, made of one or more parts.</summary>
/// <param name="SegmentId">The script segment it speaks, e.g. "S03".</param>
/// <param name="ScriptVersion">The version of that segment it speaks.</param>
/// <param name="AudioFile">The audio, relative to the projects folder.</param>
/// <param name="Seconds">The measured length of the whole audio.</param>
/// <param name="Seed">The seed every part was spoken with.</param>
public sealed record VoiceClip(
    string SegmentId,
    int ScriptVersion,
    string AudioFile,
    double Seconds,
    int Seed,
    IReadOnlyList<VoicePart> Parts,
    IReadOnlyList<SpokenWord> Words)
{
    /// <summary>Raised whenever the stored shape changes, so older clips can still be read.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The longest part sent to the TTS workflow in one go.</summary>
    public const int MaxPartSeconds = 45;
}

/// <summary>The Voice stage's whole result: which segments were spoken and how long they are together.</summary>
public sealed record VoiceSheet(IReadOnlyList<string> SegmentIds, double Seconds);

/// <summary>One segment's Voice cell as the matrix shows it.</summary>
/// <param name="AudioPath">The audio file to play; null when it is gone from the projects folder.</param>
/// <param name="Error">Why the last speaking of this segment failed; null unless the state is Failed.</param>
/// <param name="Activity">What speaking this segment again did: live while it runs, kept after it failed.</param>
public sealed record VoiceSegmentView(
    string Id,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    string? AudioPath,
    double Seconds,
    IReadOnlyList<VoicePart> Parts,
    IReadOnlyList<SpokenWord> Words,
    string? Error,
    IReadOnlyList<ActivityLine> Activity);

/// <summary>Everything the result matrix shows of the Voice stage.</summary>
/// <param name="Error">Why the last run of the whole stage failed; null unless the state is Failed.</param>
/// <param name="Seconds">The voice total: every segment's audio together.</param>
public sealed record VoiceView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    IReadOnlyList<VoiceSegmentView> Segments,
    double Seconds,
    int TargetSeconds);
