namespace StoryForge.Client;

/// <summary>Where a picture in the image pool came from.</summary>
public enum PoolOrigin
{
    /// <summary>A page of one of the project's sources.</summary>
    Found,
    /// <summary>Your own file, e.g. official artwork or a screenshot.</summary>
    Own,
}

/// <summary>
/// A picture in the project's image pool: input for Qwen only, never in the video. Found on a
/// source page or added by you, e.g. Karlach's portrait from bg3.wiki.
/// </summary>
/// <param name="Id">"P01", unique within the project.</param>
/// <param name="Url">Where it was found; your own file's name for one you added.</param>
/// <param name="Page">The source page it is on; empty for your own.</param>
/// <param name="File">The stored picture, relative to the projects folder.</param>
public sealed record PoolImage(string Id, string Url, string Page, string Caption, int Width, int Height, string File, PoolOrigin Origin)
{
    /// <summary>Qwen needs about this much on the short side to work from a picture.</summary>
    public const int MinUsableSide = 512;

    public bool TooSmall => Math.Min(Width, Height) < MinUsableSide;
}

/// <summary>Someone or something that gets reference images, e.g. Karlach.</summary>
/// <param name="Id">"R01", unique within the project.</param>
/// <param name="KeyReason">Why it carries the story when it is shown only once; empty otherwise.</param>
/// <param name="Look">What it looks like, in words: goes into every prompt that shows it.</param>
/// <param name="Shots">The keys of the shots it is in.</param>
/// <param name="Sources">The pool pictures it is made from, by id, the first one first; one too small makes nothing.</param>
/// <param name="DescriptionOnly">You chose to make it from its look alone.</param>
public sealed record Reference(
    string Id,
    string Name,
    CastKind Kind,
    string KeyReason,
    string Look,
    IReadOnlyList<string> Shots,
    IReadOnlyList<string> Sources,
    bool DescriptionOnly = false);

/// <summary>On the cast but not a reference: shown in one shot and not key to the story.</summary>
public sealed record OtherCast(string Name, CastKind Kind, int Shots);

/// <summary>Where the References stage stands: the list to confirm, or the images.</summary>
public enum ReferencePhase
{
    List,
    Images,
}

/// <summary>The References stage as a whole: who and what gets references, the pool, the rest of the cast.</summary>
public sealed record ReferenceSheet(IReadOnlyList<Reference> References, IReadOnlyList<PoolImage> Pool, IReadOnlyList<OtherCast> Others, ReferencePhase Phase)
{
    /// <summary>Raised whenever the stored shape changes, so older sheets can still be read.</summary>
    public const int SchemaVersion = 1;
}

/// <summary>How a reference image came about, as its card says it.</summary>
public enum ImageMaking
{
    /// <summary>A good picture: Qwen only cleaned it up.</summary>
    FromPicture,
    /// <summary>Only part of it was found; Qwen filled in the rest (see <see cref="ReferenceImage.Missing"/>).</summary>
    PartlyMadeUp,
    /// <summary>From the look alone, as you chose.</summary>
    FromDescription,
    /// <summary>Nothing usable was found, so nothing was made: it cannot be approved.</summary>
    NoPicture,
}

/// <summary>One reference image, e.g. Karlach's image 1: the cell keyed "R01/1".</summary>
/// <param name="File">The image, relative to the projects folder; null when nothing was made.</param>
/// <param name="Sources">The pool pictures it was made from.</param>
/// <param name="Missing">What Qwen made up ("legs, shoes"), or why nothing was made.</param>
public sealed record ReferenceImage(string ReferenceId, int Number, string? File, IReadOnlyList<string> Sources, int Seed, ImageMaking Made, string Missing)
{
    /// <summary>"R01/1".</summary>
    public static string KeyOf(string referenceId, int number) => $"{referenceId}/{number}";
}

/// <summary>One image's cell as the References screen shows it.</summary>
/// <param name="Path">The image file to show; null when nothing was made or it is gone.</param>
public sealed record ReferenceImageView(
    string Key,
    int Number,
    StageState State,
    int Version,
    int? ApprovedVersion,
    IReadOnlyList<ResultVersion> Versions,
    ReferenceImage Image,
    string? Path,
    string? Error,
    IReadOnlyList<ActivityLine> Activity);

/// <summary>A reference with its images, as a card shows it.</summary>
public sealed record ReferenceView(Reference Reference, IReadOnlyList<ReferenceImageView> Images);

/// <summary>A pool picture as the screen shows it, with the references it is used for.</summary>
/// <param name="Path">The picture file to show; null when it is gone.</param>
public sealed record PoolImageView(PoolImage Image, string? Path, IReadOnlyList<string> UsedFor);

/// <summary>Everything the References screen shows.</summary>
/// <param name="TextOnly">The project makes its stills from text only: no references are needed.</param>
/// <param name="ShotLabels">Each shot key's label ("S01·2"), for "in 5 shots: S01·2, …".</param>
public sealed record ReferencesView(
    Guid ProjectId,
    StageState State,
    string? Error,
    IReadOnlyList<ActivityLine> Activity,
    ReferencePhase Phase,
    IReadOnlyList<ReferenceView> References,
    IReadOnlyList<PoolImageView> Pool,
    IReadOnlyList<OtherCast> Others,
    bool TextOnly,
    IReadOnlyDictionary<string, string> ShotLabels);

/// <summary>What a change to the reference list does.</summary>
public enum ReferenceChangeKind
{
    /// <summary>A new reference: <see cref="ReferenceChange.Name"/> and <see cref="ReferenceChange.Kind"/>.</summary>
    Add,
    /// <summary>A name from the rest of the cast becomes a reference: <see cref="ReferenceChange.Name"/>.</summary>
    MakeReference,
    Remove,
    /// <summary><see cref="ReferenceChange.ReferenceId"/> goes into <see cref="ReferenceChange.OtherId"/>.</summary>
    Merge,
    /// <summary>The reference's look: <see cref="ReferenceChange.Look"/>.</summary>
    EditLook,
    /// <summary>The reference is made from <see cref="ReferenceChange.PoolImageId"/> too.</summary>
    UseSource,
    /// <summary>The reference is no longer made from <see cref="ReferenceChange.PoolImageId"/>.</summary>
    DropSource,
    /// <summary><see cref="ReferenceChange.PoolImageId"/> leaves the pool and every reference.</summary>
    DiscardFromPool,
}

/// <summary>One change to the reference list or the pool; the fields its kind names are set.</summary>
public sealed record ReferenceChange(
    ReferenceChangeKind Change,
    string? ReferenceId = null,
    string? OtherId = null,
    string? Name = null,
    CastKind Kind = CastKind.Character,
    string? Look = null,
    string? PoolImageId = null);
