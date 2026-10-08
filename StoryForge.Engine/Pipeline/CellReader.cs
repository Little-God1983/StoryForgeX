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
}
