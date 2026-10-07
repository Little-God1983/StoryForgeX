namespace StoryForge.Client;

/// <summary>The stages of a run, in order, as the run plan lists them.</summary>
public enum PipelineStage
{
    Research,
    Script,
    Voice,
    Storyboard,
    References,
    Stills,
    Clips,
    Assembly,
    Publish,
}

/// <summary>Where one stage of a project stands.</summary>
public enum StageState
{
    NotStarted,
    Running,
    /// <summary>Done and waiting at its gate for you to approve it.</summary>
    NeedsReview,
    Approved,
    Failed,
    /// <summary>Something it was made from changed since (set from #11 on).</summary>
    Stale,
}

public enum RunMode
{
    StopAtGates,
    RunThrough,
}

/// <summary>How the stills keep characters and places the same from shot to shot.</summary>
public enum Consistency
{
    ReferenceImages,
    TextToImageOnly,
}

public enum AssemblyTarget
{
    FfmpegWithFcpxml,
    DavinciResolve,
}

/// <summary>One exact profile version, as a project uses it. Saving the profile later changes nothing here.</summary>
public sealed record ProfileRef(Guid ProfileId, int Version);

/// <summary>The "Research &amp; script" card: one provider and model for research, script and storyboard.</summary>
public sealed record WritingSetup(string Provider, string Model, ProfileRef Research, ProfileRef Script, ProfileRef Storyboard);

public sealed record VoiceSetup(string Provider, string Model, ProfileRef Profile);

/// <param name="Size">The size stills are generated at (not the delivery size).</param>
public sealed record StillsSetup(string Provider, string Model, ProfileRef Profile, Consistency Consistency, GenerationSize Size);

/// <param name="Size">The size clips are generated at; the finished video is upscaled to delivery.</param>
public sealed record ClipsSetup(string Provider, string Model, ProfileRef Profile, int MaxClipSeconds, GenerationSize Size);

/// <param name="Aspect">"16:9", "9:16" or "1:1".</param>
/// <param name="Width">Delivery width, e.g. 1920.</param>
/// <param name="TargetSeconds">How long the finished video should be.</param>
public sealed record OutputSetup(string Aspect, int Width, int Height, int TargetSeconds, string Language, AssemblyTarget Assembly);

/// <summary>Every choice on the New project screen. Stored with the project, so a rerun reproduces it.</summary>
/// <param name="Gates">The stages after which the run pauses for approval.</param>
public sealed record ProjectSetup(
    string Name,
    string Brief,
    IReadOnlyList<string> ResearchSources,
    WritingSetup Writing,
    VoiceSetup Voice,
    StillsSetup Stills,
    ClipsSetup Clips,
    OutputSetup Output,
    IReadOnlyList<PipelineStage> Gates,
    RunMode Mode)
{
    /// <summary>Gates that cannot be switched off: the storyboard and the references are always reviewed.</summary>
    public static IReadOnlyList<PipelineStage> RequiredGates { get; } = [PipelineStage.Storyboard, PipelineStage.References];
}

public sealed record StageStatus(PipelineStage Stage, StageState State);

/// <summary>A saved project: its setup and where each stage stands.</summary>
public sealed record Project(Guid Id, DateTimeOffset CreatedAt, ProjectSetup Setup, IReadOnlyList<StageStatus> Stages);
