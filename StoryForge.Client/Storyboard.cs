namespace StoryForge.Client;

/// <summary>
/// How long shots should be: "shots of 3–8 s, about 5 s". Within the range the storyboard cuts
/// from the story itself; the hard limits (<see cref="ShortestShot"/>, the video model's longest
/// clip) hold whatever the pacing says.
/// </summary>
public sealed record Pacing(int MinSeconds, int TypicalSeconds, int MaxSeconds)
{
    public static Pacing Default { get; } = new(3, 5, 8);

    /// <summary>No shot is shorter, unless its segment's whole audio is.</summary>
    public const int ShortestShot = 2;

    /// <summary>Why this pacing cannot be used; null when it can.</summary>
    public string? Problem(int? maxClipSeconds = null) =>
        MinSeconds < ShortestShot ? $"Shots must be at least {ShortestShot} s long, not {MinSeconds} s."
        : TypicalSeconds < MinSeconds || TypicalSeconds > MaxSeconds ? $"The typical shot ({TypicalSeconds} s) must lie between {MinSeconds} s and {MaxSeconds} s."
        : maxClipSeconds is { } clip && MaxSeconds > clip ? $"Shots of up to {MaxSeconds} s are longer than the video model makes ({clip} s)."
        : null;

    /// <summary>"3–8 s, about 5 s".</summary>
    public override string ToString() => $"{MinSeconds}–{MaxSeconds} s, about {TypicalSeconds} s";
}

public enum CastKind
{
    Character,
    Place,
    Object,
}

/// <summary>Someone or something the storyboard shows, e.g. Karlach, the House of Hope, a soul coin.</summary>
/// <param name="KeyReason">Why it carries the story although it is shown only once; empty otherwise.</param>
public sealed record CastMember(string Name, CastKind Kind, string KeyReason);

/// <summary>
/// One shot: one clip, within one segment. It covers <paramref name="WordCount"/> words of the
/// segment's voice from <paramref name="FirstWord"/>, and lasts from <paramref name="Start"/> to
/// <paramref name="End"/> in the segment's audio.
/// </summary>
/// <param name="Key">Stable for the shot's life, e.g. "S01/3f9a"; the screen numbers shots by position instead.</param>
/// <param name="Cast">Who and what is in it, at most <see cref="MaxCast"/>.</param>
/// <param name="ContinuesPrevious">Same scene as the shot before, the camera moves on.</param>
public sealed record Shot(
    string Key,
    string SegmentId,
    int FirstWord,
    int WordCount,
    double Start,
    double End,
    string Description,
    IReadOnlyList<string> Cast,
    bool ContinuesPrevious)
{
    /// <summary>Raised whenever the stored shape changes, so older shots can still be read.</summary>
    public const int SchemaVersion = 1;

    /// <summary>What Minimax takes per clip.</summary>
    public const int MaxCast = 4;

    public double Seconds => End - Start;
}

/// <summary>The storyboard as a whole: who and what it shows, and what it was cut from.</summary>
/// <param name="VoiceVersions">Per segment, the voice version whose word timings the shots follow.</param>
public sealed record StoryboardSheet(IReadOnlyList<CastMember> Cast, IReadOnlyDictionary<string, int> VoiceVersions, Pacing Pacing);

/// <summary>One shot's Board cell as the matrix shows it.</summary>
/// <param name="Number">Its place in its segment, from 1: shown as "S01·2".</param>
/// <param name="Error">Why the last regeneration of this shot failed; null unless the state is Failed.</param>
public sealed record ShotView(
    string Key,
    string SegmentId,
    int Number,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    Shot Shot,
    string? Error,
    IReadOnlyList<ActivityLine> Activity)
{
    /// <summary>"S01·2".</summary>
    public string Label => $"{SegmentId}·{Number}";

    /// <summary>
    /// The versions that start or end elsewhere than the shot does now. They cannot come back: they
    /// would overlap the shot next to it, or leave words no shot covers.
    /// </summary>
    public IReadOnlyList<int> MovedVersions { get; init; } = [];
}

/// <summary>Everything the matrix shows of the Storyboard stage.</summary>
/// <param name="Shots">In order: by segment, then by where they start.</param>
/// <param name="Words">Per segment, the spoken words the shots were cut from.</param>
public sealed record StoryboardView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    IReadOnlyList<ShotView> Shots,
    IReadOnlyList<CastMember> Cast,
    IReadOnlyDictionary<string, IReadOnlyList<SpokenWord>> Words,
    Pacing Pacing,
    int MaxClipSeconds);

/// <summary>Your change to a shot; a null field stays as it is.</summary>
public sealed record ShotChange(string? Description = null, IReadOnlyList<string>? Cast = null, bool? ContinuesPrevious = null);

/// <summary>Which cut of a shot moves: where it starts or where it ends.</summary>
public enum ShotEdge
{
    Start,
    End,
}
