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
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == PipelineStage.Research && c.Key == "", cancellationToken);
        if (cell?.ApprovedVersion is not { } version)
        {
            return null;
        }
        var entry = await db.CellVersions.AsNoTracking().FirstAsync(
            v => v.ProjectId == projectId && v.Stage == PipelineStage.Research && v.Key == "" && v.Version == version, cancellationToken);
        return (StoredJson.Read<FactSheet>(entry.OutputJson), version);
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
