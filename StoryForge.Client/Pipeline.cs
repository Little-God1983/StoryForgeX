namespace StoryForge.Client;

/// <summary>What one line of a stage's activity is about, e.g. a search or a page read.</summary>
public enum ActivityKind
{
    Search,
    Fetch,
    /// <summary>A page the stage was not allowed to reach (not a project source).</summary>
    Refused,
    /// <summary>A search or page that failed: the site was down, the page does not exist.</summary>
    Failed,
    /// <summary>The model at work, e.g. "writing the fact sheet…".</summary>
    Model,
    /// <summary>The engine checking the model's answer, e.g. "try 1: F04 has no source link – sent back".</summary>
    Check,
}

/// <summary>One line of what a stage did, as its screen lists it while it runs and afterwards.</summary>
public sealed record ActivityLine(DateTimeOffset At, ActivityKind Kind, string Text);

public enum VersionOrigin
{
    /// <summary>Made by the stage.</summary>
    Generated,
    /// <summary>Your changes to an earlier version.</summary>
    Edited,
}

/// <summary>One version of a stage's result: "v2 · your edits of v1".</summary>
/// <param name="BasedOn">For an edited version, the version it was edited from.</param>
public sealed record ResultVersion(int Version, VersionOrigin Origin, DateTimeOffset CreatedAt, int? BasedOn);

/// <summary>A stage changed: its state, or a new line of activity while it runs.</summary>
public sealed record StageUpdate(Guid ProjectId, PipelineStage Stage, StageState State, ActivityLine? Activity = null);
