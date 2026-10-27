namespace StoryForge.Client;

/// <summary>One shot's still: the cell keyed by the shot's key.</summary>
/// <param name="File">The picture, relative to the projects folder.</param>
/// <param name="Prompt">What the image model was asked for: the profile's template with the shot's prompt in it.</param>
/// <param name="Workflow">The profile's workflow file it was made with; empty for your own file.</param>
/// <param name="References">The reference images it was made from, by key ("R01/1"), the first one first.</param>
/// <param name="ContinuesFrom">The shot whose still it started from (a continuation); null for none.</param>
/// <param name="YourPrompt">Made from your prompt (Edit prompt…), not one Claude wrote.</param>
/// <param name="YourFile">Your own picture (Replace with file…): nothing was made.</param>
public sealed record Still(
    string ShotKey,
    string File,
    string Prompt,
    int Seed,
    GenerationSize Size,
    string Workflow,
    IReadOnlyList<string> References,
    string? ContinuesFrom = null,
    bool YourPrompt = false,
    bool YourFile = false)
{
    /// <summary>Raised whenever the stored shape changes, so older stills can still be read.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>One shot's Still cell as the matrix shows it.</summary>
/// <param name="Still">The version shown; null before the first was made.</param>
/// <param name="Path">The picture to show; null before it was made, or when the file is gone.</param>
/// <param name="InputHash">What the version shown was made from, as a hash; empty before it was made.</param>
/// <param name="Queued">Regenerate was clicked: it is made again with the batch, on Continue pipeline.</param>
/// <param name="QueuedPrompt">Your prompt it is made again with (Edit prompt…); null for one Claude writes.</param>
public sealed record StillView(
    string Key,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    Still? Still,
    string? Path,
    string InputHash,
    bool Queued,
    string? QueuedPrompt,
    string? Error,
    IReadOnlyList<ActivityLine> Activity);

/// <summary>Everything the matrix shows of the Stills stage.</summary>
/// <param name="Stills">One per shot that has a Still cell, by shot key; a shot without one has no still yet.</param>
/// <param name="ReferencesApproved">The references are approved, so the stills can be made.</param>
public sealed record StillsView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    IReadOnlyList<StillView> Stills,
    bool ReferencesApproved);

/// <summary>A test picture from an image profile, made on the Profiles screen; it goes into no project.</summary>
/// <param name="Path">The picture file.</param>
/// <param name="Prompt">The test line in the profile's prompt template, as the model got it.</param>
public sealed record TestRender(string Path, string Prompt, int Seed, GenerationSize Size, TimeSpan Took);
