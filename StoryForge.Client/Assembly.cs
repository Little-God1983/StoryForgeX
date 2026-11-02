namespace StoryForge.Client;

/// <summary>How one shot gives way to the next.</summary>
public enum CutTransition
{
    /// <summary>A hard cut: the picture changes from one frame to the next.</summary>
    Cut,
    Crossfade,
    /// <summary>The picture fades to black and back, e.g. at the end of a chapter.</summary>
    DipToBlack,
}

/// <summary>
/// The cut after one shot. The narration is one fixed track and the shots keep the storyboard's
/// order, so a cut only moves inside the clips' handles (half a second before and after each shot).
/// </summary>
/// <param name="After">The key of the shot it ends; the next shot in storyboard order begins.</param>
/// <param name="Seconds">How long the crossfade or dip lasts; 0 for a hard cut.</param>
/// <param name="Shift">Where the picture changes against the narration: -0.2 is 0.2 s before the next shot's words start.</param>
/// <param name="SoundLead">
/// When the clips' own sound changes against the picture: 0.3 is 0.3 s before it (J cut, the next
/// clip is heard first), -0.3 is 0.3 s after it (L cut, this clip is still heard).
/// </param>
/// <param name="Why">Claude's reason, as the panel shows it; empty for none.</param>
/// <param name="Yours">You changed it.</param>
public sealed record Cut(string After, CutTransition Transition, double Seconds, double Shift, double SoundLead, string Why, bool Yours = false)
{
    /// <summary>The same cut as far as the video goes: the transition and the times, not the reason or whose it is.</summary>
    public bool SameAs(Cut other) =>
        Transition == other.Transition && Near(Seconds, other.Seconds) && Near(Shift, other.Shift) && Near(SoundLead, other.SoundLead);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.0005;
}

/// <summary>Where music would go: unused until the music bed exists (#24).</summary>
public sealed record MusicCue(double Start, double End, string Mood);

/// <summary>Every cut of the video, in shot order, and how loud the clips' own sound is under the narration.</summary>
/// <param name="ClipSoundDb">The clips' sound under the narration, e.g. -20; the narration stays at 0 dB.</param>
public sealed record CutList(IReadOnlyList<Cut> Cuts, double ClipSoundDb, IReadOnlyList<MusicCue> Music)
{
    /// <summary>The quiet bed the clips' sound starts as.</summary>
    public const double DefaultClipSoundDb = -20;

    /// <summary>Quieter than this is as good as muted.</summary>
    public const double MinClipSoundDb = -60;

    /// <summary>The longest crossfade or dip: it eats into both clips' handles.</summary>
    public const double MaxTransitionSeconds = 1.0;

    /// <summary>The shortest crossfade or dip that reads as one.</summary>
    public const double MinTransitionSeconds = 0.2;

    /// <summary>The furthest the clips' sound changes before or after the picture.</summary>
    public const double MaxSoundLead = 1.0;
}

/// <summary>One version of the Assembly: a cut list and the video rendered from it.</summary>
/// <param name="File">The video, relative to the projects folder.</param>
/// <param name="Seconds">How long it runs: the narration's length.</param>
/// <param name="Size">The delivery size it was rendered at; larger clips are cropped to it.</param>
/// <param name="Clips">Per shot key, the version of its clip it was cut from.</param>
/// <param name="Voices">Per segment, the version of its voice it was cut to.</param>
/// <param name="BasedOn">For your edit of a cut list, the version it was edited from; null for Claude's.</param>
/// <param name="Timeline">The same cut as an FCPXML timeline, relative to the projects folder; null when none was written (#71).</param>
public sealed record AssembledVideo(
    CutList CutList,
    string File,
    double Seconds,
    GenerationSize Size,
    IReadOnlyDictionary<string, int> Clips,
    IReadOnlyDictionary<string, int> Voices,
    int? BasedOn = null,
    string? Timeline = null)
{
    /// <summary>Raised whenever the stored shape changes, so older videos can still be read.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>One shot on the Assembly timeline: where it sits in the narration and how much clip it has to spare.</summary>
/// <param name="Label">"S01·2".</param>
/// <param name="Start">When its words start in the whole narration.</param>
/// <param name="End">When the next shot's words start (the end of the narration for the last shot).</param>
/// <param name="Handle">The clip's spare seconds before and after the shot, which a cut can move into.</param>
/// <param name="Heard">The words heard while it is on screen.</param>
/// <param name="ClipPath">Its approved clip, full size, to play; null when the file is gone.</param>
public sealed record AssemblyShot(string Key, string Label, double Start, double End, double Handle, string Heard, string? ClipPath);

/// <summary>Everything the Assembly screen shows.</summary>
/// <param name="Version">The version shown; null before the first was made.</param>
/// <param name="Video">That version's cut list and video; null before the first.</param>
/// <param name="Path">The video to play; null before it was made, or when the file is gone.</param>
/// <param name="Shots">The shots in storyboard order, timed to the narration: one block each on the timeline.</param>
/// <param name="ClipsApproved">Every clip is approved, so the video can be cut.</param>
/// <param name="Waiting">Your cut list, waiting to be rendered (Render again was clicked, the render failed or was cancelled); null for none.</param>
public sealed record AssemblyView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    int? Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    AssembledVideo? Video,
    string? Path,
    IReadOnlyList<AssemblyShot> Shots,
    bool ClipsApproved,
    CutList? Waiting)
{
    /// <summary>Why it is out of date (stale), e.g. "S02·3's clip changed since the video was cut"; null while it is up to date.</summary>
    public string? Stale { get; init; }

    /// <summary>The version your waiting cut list was edited from; null when none waits, or it was the current one.</summary>
    public int? WaitingBasedOn { get; init; }

    /// <summary>The shown version's FCPXML timeline, full path; null when none was written or the file is gone.</summary>
    public string? TimelinePath { get; init; }
}

/// <summary>What Send to Resolve did.</summary>
/// <param name="Ok">The project exists and the timeline is in it; <see cref="Problem"/> may still say what did not go to plan.</param>
/// <param name="Stage">How far it got: "install", "launch", "connect", "create", "rate", "import", "open", "done".</param>
/// <param name="Problem">Why it failed, or what went wrong after the import; null when all went well.</param>
/// <param name="TimelineName">The timeline's name in Resolve.</param>
/// <param name="Clips">How many clips the timeline's first video track holds.</param>
public sealed record SentToResolve(bool Ok, string Stage, string? Problem, string? TimelineName, int Clips);
