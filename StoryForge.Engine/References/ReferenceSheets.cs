using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;

namespace StoryForge.Engine.References;

/// <summary>
/// Writes the References sheet as the stage's next version: your edits on the screen, and the look
/// Claude saw while one image was made on its own.
/// </summary>
internal sealed class ReferenceSheets(IDbContextFactory<StoryForgeDbContext> contextFactory, TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.References;

    /// <summary>The sheet as an edited version of the one before; a removed reference's images go with it.</summary>
    /// <returns>The image keys removed, and the stage's state.</returns>
    public async Task<(IReadOnlyList<string> Removed, StageState State)> WriteAsync(
        Guid projectId, ReferenceSheet sheet, IReadOnlyList<string> removed, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        var current = await db.CellVersions.FirstAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "" && v.Version == cell.CurrentVersion, cancellationToken);
        var next = 1 + await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "").MaxAsync(v => v.Version, cancellationToken);
        var now = clock.GetUtcNow();
        db.CellVersions.Add(new CellVersionEntry
        {
            ProjectId = projectId,
            Stage = Stage,
            Key = "",
            Version = next,
            Origin = VersionOrigin.Edited,
            BasedOn = current.Version,
            InputHash = current.InputHash,
            SchemaVersion = ReferenceSheet.SchemaVersion,
            OutputJson = StoredJson.Write(sheet),
            CreatedAt = now,
        });
        cell.CurrentVersion = next;
        cell.UpdatedAt = now;
        foreach (var id in removed)
        {
            var images = await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == Stage && c.Key.StartsWith(id + "/")).ToListAsync(cancellationToken);
            db.Cells.RemoveRange(images);
            keys.AddRange(images.Select(i => i.Key));
        }
        if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
        {
            project.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return (keys, cell.State);
    }
}
