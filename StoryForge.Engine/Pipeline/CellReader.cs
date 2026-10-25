using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine.Pipeline;

/// <summary>Reads what a stage works from: the approved result of the stage before it, and its own segments.</summary>
internal sealed class CellReader(IDbContextFactory<StoryForgeDbContext> contextFactory)
{
    /// <summary>The approved fact sheet and its version; null while none is approved.</summary>
    public async Task<(FactSheet Sheet, int Version)?> ApprovedFactSheetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        int? version;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            version = await db.Cells.AsNoTracking()
                .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Research && c.Key == "")
                .Select(c => c.ApprovedVersion)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return version is { } approved && await FactSheetAsync(projectId, approved, cancellationToken) is { } sheet ? (sheet, approved) : null;
    }

    /// <summary>One version of the fact sheet; null when there is no such version.</summary>
    public async Task<FactSheet?> FactSheetAsync(Guid projectId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.Research && v.Key == "" && v.Version == version, cancellationToken);
        return entry is null ? null : StoredJson.Read<FactSheet>(entry.OutputJson);
    }

    /// <summary>The whole script as it was last written; null before it was.</summary>
    public async Task<ScriptSheet?> CurrentScriptAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var version = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Script && c.Key == "")
            .Select(c => c.CurrentVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var entry = version is null ? null : await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.Script && v.Key == "" && v.Version == version, cancellationToken);
        return entry is null ? null : StoredJson.Read<ScriptSheet>(entry.OutputJson);
    }

    /// <summary>
    /// The script's segments as approved, with their versions, in order: what the voice speaks. A
    /// segment not approved (yet) counts with its current version.
    /// </summary>
    public async Task<IReadOnlyList<(Segment Segment, int Version)>> ApprovedSegmentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cells = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Script && c.Key != "")
            .ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == PipelineStage.Script && v.Key != "")
            .ToListAsync(cancellationToken);
        return
        [
            .. cells.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => versions.FirstOrDefault(v => v.Key == c.Key && v.Version == (c.ApprovedVersion ?? c.CurrentVersion)))
                .OfType<CellVersionEntry>()
                .Select(v => (StoredJson.Read<Segment>(v.OutputJson), v.Version)),
        ];
    }

    /// <summary>The script's segments as they stand: each segment's current version, in order.</summary>
    public async Task<IReadOnlyList<Segment>> CurrentSegmentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cells = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Script && c.Key != "")
            .ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == PipelineStage.Script && v.Key != "")
            .ToListAsync(cancellationToken);
        return
        [
            .. cells.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => versions.FirstOrDefault(v => v.Key == c.Key && v.Version == c.CurrentVersion))
                .OfType<CellVersionEntry>()
                .Select(v => StoredJson.Read<Segment>(v.OutputJson)),
        ];
    }

    /// <summary>
    /// The voice of every segment as approved, with its version, in segment order: what the
    /// storyboard is cut from. A segment not approved (yet) counts with its current version.
    /// </summary>
    public async Task<IReadOnlyList<(VoiceClip Clip, int Version)>> ApprovedVoicesAsync(Guid projectId, CancellationToken cancellationToken) =>
        [.. (await ReadSegmentsAsync(projectId, PipelineStage.Voice, c => c.ApprovedVersion ?? c.CurrentVersion, cancellationToken))
            .Select(v => (StoredJson.Read<VoiceClip>(v.OutputJson), v.Version))];

    /// <summary>One version of a segment's voice; null when there is no such version.</summary>
    public async Task<VoiceClip?> VoiceAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.Voice && v.Key == segmentId && v.Version == version, cancellationToken);
        return entry is null ? null : StoredJson.Read<VoiceClip>(entry.OutputJson);
    }

    /// <summary>The storyboard as it was last planned as a whole; null before it was.</summary>
    public async Task<StoryboardSheet?> CurrentStoryboardAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var version = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Storyboard && c.Key == "")
            .Select(c => c.CurrentVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var entry = version is null ? null : await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.Storyboard && v.Key == "" && v.Version == version, cancellationToken);
        return entry is null ? null : StoredJson.Read<StoryboardSheet>(entry.OutputJson);
    }

    /// <summary>Where a stage stands as a whole; not started before it ever ran.</summary>
    public async Task<StageState> StateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key == "")
            .Select(c => (StageState?)c.State)
            .FirstOrDefaultAsync(cancellationToken) ?? StageState.NotStarted;
    }

    /// <summary>The segments of a stage that were never made: their first run failed or was cancelled.</summary>
    public async Task<IReadOnlyList<string>> UnmadeKeysAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key != "" && c.CurrentVersion == null && c.State != StageState.Running)
            .OrderBy(c => c.Key)
            .Select(c => c.Key)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The References stage's sheet as it stands: the list, the pool, the phase; null before it was proposed.</summary>
    public async Task<ReferenceSheet?> CurrentReferencesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var version = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.References && c.Key == "")
            .Select(c => c.CurrentVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var entry = version is null ? null : await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.References && v.Key == "" && v.Version == version, cancellationToken);
        return entry is null ? null : StoredJson.Read<ReferenceSheet>(entry.OutputJson);
    }

    /// <summary>Every reference image as it stands, each in its current version.</summary>
    public async Task<IReadOnlyList<ReferenceImage>> ReferenceImagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        [.. (await ReadSegmentsAsync(projectId, PipelineStage.References, c => c.CurrentVersion, cancellationToken)).Select(v => StoredJson.Read<ReferenceImage>(v.OutputJson))];

    /// <summary>The shots as they stand, each in its current version: by segment, then where they start.</summary>
    public async Task<IReadOnlyList<Shot>> CurrentShotsAsync(Guid projectId, CancellationToken cancellationToken) =>
        [.. (await ReadSegmentsAsync(projectId, PipelineStage.Storyboard, c => c.CurrentVersion, cancellationToken))
            .Select(v => StoredJson.Read<Shot>(v.OutputJson))
            .OrderBy(s => s.SegmentId, StringComparer.Ordinal).ThenBy(s => s.FirstWord)];

    /// <summary>
    /// Every key a shot of the storyboard has a version under. A shot merged or planned away loses
    /// its versions with its cell, so its key may come back, with no history attached.
    /// </summary>
    public async Task<IReadOnlyList<string>> UsedShotKeysAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == PipelineStage.Storyboard && v.Key != "")
            .Select(v => v.Key).Distinct().ToListAsync(cancellationToken);
    }

    /// <summary>One version per segment cell of a stage, by key: the one <paramref name="version"/> picks.</summary>
    private async Task<IReadOnlyList<CellVersionEntry>> ReadSegmentsAsync(Guid projectId, PipelineStage stage, Func<CellEntry, int?> version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cells = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key != "")
            .ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == stage && v.Key != "")
            .ToListAsync(cancellationToken);
        return
        [
            .. cells.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => versions.FirstOrDefault(v => v.Key == c.Key && v.Version == version(c)))
                .OfType<CellVersionEntry>(),
        ];
    }
}
