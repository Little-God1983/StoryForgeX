using StoryForge.Client;

namespace StoryForge.Engine.Data;

/// <summary>
/// One result cell of a project: a stage with one result per project (Research), or one shot of
/// a stage with a result per shot (from the script on). Its versions are <see cref="CellVersionEntry"/>.
/// </summary>
public sealed class CellEntry
{
    public Guid ProjectId { get; set; }

    public PipelineStage Stage { get; set; }

    /// <summary>"" for a stage with one result per project; the shot from the script on.</summary>
    public string Key { get; set; } = "";

    public StageState State { get; set; }

    /// <summary>The version the cell shows and the next stage would use; null before the first.</summary>
    public int? CurrentVersion { get; set; }

    public int? ApprovedVersion { get; set; }

    /// <summary>Why the last run failed; null unless the state is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>What the last run did, as a JSON list of <see cref="ActivityLine"/>.</summary>
    public string ActivityJson { get; set; } = "[]";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One version of a cell's result. Never changed once approved or once a later version exists.</summary>
public sealed class CellVersionEntry
{
    public Guid ProjectId { get; set; }

    public PipelineStage Stage { get; set; }

    public string Key { get; set; } = "";

    public int Version { get; set; }

    public VersionOrigin Origin { get; set; }

    /// <summary>For an edited version, the version it was edited from.</summary>
    public int? BasedOn { get; set; }

    /// <summary>
    /// A hash of everything the result was made from: upstream versions, provider, model, profile
    /// version, parameters. When it no longer matches, the result is out of date (#11).
    /// </summary>
    public required string InputHash { get; set; }

    /// <summary>The shape <see cref="OutputJson"/> was written in, e.g. <see cref="FactSheet.SchemaVersion"/>.</summary>
    public int SchemaVersion { get; set; }

    public required string OutputJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
