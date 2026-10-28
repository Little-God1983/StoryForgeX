namespace StoryForge.Client;

/// <summary>One shot's clip: the cell keyed by the shot's key, animated from the shot's approved still.</summary>
/// <param name="File">The video, relative to the projects folder.</param>
/// <param name="Prompt">The motion prompt the video model got: the profile's template with the shot's prompt in it.</param>
/// <param name="Seconds">How long it was asked to be: the shot and its handles. The model may round it up to its own frame grid.</param>
/// <param name="Handle">The extra seconds before and after the shot, for the cuts; less when the shot is near the model's longest clip.</param>
/// <param name="Model">What made it, e.g. "Minimax H3", or the video profile it was switched to.</param>
/// <param name="Workflow">The video profile's workflow file it was made with.</param>
/// <param name="StillVersion">The version of the shot's still it starts from.</param>
/// <param name="YourPrompt">Made from your prompt (Edit prompt…), not one Claude wrote.</param>
public sealed record Clip(
    string ShotKey,
    string File,
    string Prompt,
    int Seed,
    GenerationSize Size,
    double Seconds,
    double Handle,
    string Model,
    string Workflow,
    int StillVersion,
    bool YourPrompt = false)
{
    /// <summary>Raised whenever the stored shape changes, so older clips can still be read.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>One shot's Clip cell as the matrix shows it.</summary>
/// <param name="Clip">The version shown; null before the first was made.</param>
/// <param name="Path">The video to play; null before it was made, or when the file is gone.</param>
/// <param name="Queued">Regenerate was clicked: it is made again with the others, on Make clips.</param>
/// <param name="QueuedPrompt">Your prompt it is made again with (Edit prompt…); null for one Claude writes.</param>
/// <param name="QueuedProfile">The video profile it is made again with instead of the project's (the fallback); null for the project's.</param>
public sealed record ClipView(
    string Key,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    Clip? Clip,
    string? Path,
    string InputHash,
    bool Queued,
    string? QueuedPrompt,
    ProfileRef? QueuedProfile,
    string? Error,
    IReadOnlyList<ActivityLine> Activity)
{
    /// <summary>Why it is out of date (stale), e.g. "The still changed since the clip was made"; null while it is up to date.</summary>
    public string? Stale { get; init; }
}

/// <summary>A video profile a clip can be made with instead of the project's: "LTX 2.5 · v2".</summary>
public sealed record ClipModel(ProfileRef Profile, string Name);

/// <summary>Everything the matrix shows of the Clips stage.</summary>
/// <param name="Clips">One per shot that has a Clip cell, by shot key; a shot without one has no clip yet.</param>
/// <param name="StillsApproved">Every still is approved, so the clips can be made.</param>
/// <param name="Model">What the project makes its clips with, e.g. "Minimax H3".</param>
/// <param name="Others">The other video profiles one clip can be switched to (the fallback); the app never switches on its own.</param>
public sealed record ClipsView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    IReadOnlyList<ClipView> Clips,
    bool StillsApproved,
    string Model,
    IReadOnlyList<ClipModel> Others);
