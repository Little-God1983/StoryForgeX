namespace StoryForge.Client;

/// <summary>
/// One fact the research found. The script may use only the facts of the approved fact sheet that
/// are not left out, and must use every fact weighted <see cref="MustWeight"/>.
/// </summary>
/// <param name="Id">"F01", unique within the sheet.</param>
/// <param name="SourceUrl">The page on one of the project's sources the fact was taken from.</param>
/// <param name="Quote">The passage on that page, word for word as it was read.</param>
/// <param name="Weight">1 = mention it only if there is time left, 5 = normal, 10 = must be in the video.</param>
/// <param name="LeftOut">You took it out: the script never sees it.</param>
public sealed record Fact(string Id, string Statement, string SourceUrl, string Quote, int Weight = Fact.DefaultWeight, bool LeftOut = false)
{
    public const int MinWeight = 1;
    public const int DefaultWeight = 5;
    public const int MaxWeight = 10;
    public const int MustWeight = MaxWeight;
}

/// <summary>The Research stage's result.</summary>
public sealed record FactSheet(IReadOnlyList<Fact> Facts)
{
    /// <summary>Raised whenever the stored shape changes, so older sheets can still be read.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>Everything the fact sheet screen shows for a project.</summary>
/// <param name="Error">Why the last run failed; null unless <paramref name="State"/> is Failed.</param>
/// <param name="Version">The version <paramref name="Sheet"/> is; null before the first one.</param>
/// <param name="ApprovedVersion">The version you approved; null while none is.</param>
/// <param name="Activity">What the last run did, oldest first.</param>
public sealed record FactSheetView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ResultVersion> Versions,
    int? Version,
    int? ApprovedVersion,
    FactSheet? Sheet,
    IReadOnlyList<ActivityLine> Activity);

/// <summary>A change to one fact; what is null stays as it is.</summary>
public sealed record FactChange(string? Statement = null, int? Weight = null, bool? LeftOut = null);
