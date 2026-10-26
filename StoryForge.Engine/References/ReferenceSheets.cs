using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.References;

/// <summary>
/// Writes the References sheet as the stage's next version: your edits on the screen, and the look
/// Claude saw while one image was made on its own.
/// </summary>
internal sealed class ReferenceSheets(IDbContextFactory<StoryForgeDbContext> contextFactory, ProjectFolders folders, TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.References;

    /// <summary>The sheet as an edited version of the one before (or the first, as a run proposed it); a removed reference's images go with it, and their files.</summary>
    /// <param name="removed">References whose images go ("R01"), or single images ("R01/2").</param>
    /// <returns>The image keys removed, and the stage's state.</returns>
    public async Task<(IReadOnlyList<string> Removed, StageState State)> WriteAsync(
        Guid projectId, ReferenceSheet sheet, IReadOnlyList<string> removed, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var files = new List<string>();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        var current = cell.CurrentVersion is { } at
            ? await db.CellVersions.FirstAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "" && v.Version == at, cancellationToken)
            : null;
        var next = 1 + (await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "").MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0);
        var now = clock.GetUtcNow();
        db.CellVersions.Add(new CellVersionEntry
        {
            ProjectId = projectId,
            Stage = Stage,
            Key = "",
            Version = next,
            Origin = current is null ? VersionOrigin.Generated : VersionOrigin.Edited,
            BasedOn = current?.Version,
            InputHash = current?.InputHash ?? InputHash.Of(new { stage = Stage, sheet }),
            SchemaVersion = ReferenceSheet.SchemaVersion,
            OutputJson = StoredJson.Write(sheet),
            CreatedAt = now,
        });
        cell.CurrentVersion = next;
        // An approved stage stays approved with the list as you changed it; one image made again takes the approval back.
        cell.ApprovedVersion = cell.State == StageState.Approved ? next : cell.ApprovedVersion;
        cell.UpdatedAt = now;
        foreach (var id in removed)
        {
            var images = await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == Stage && (c.Key == id || c.Key.StartsWith(id + "/"))).ToListAsync(cancellationToken);
            var made = await db.CellVersions
                .Where(v => v.ProjectId == projectId && v.Stage == Stage && (v.Key == id || v.Key.StartsWith(id + "/")))
                .Select(v => v.OutputJson)
                .ToListAsync(cancellationToken);
            files.AddRange(made.Select(json => StoredJson.Read<ReferenceImage>(json).File).OfType<string>());
            db.Cells.RemoveRange(images);
            keys.AddRange(images.Select(i => i.Key));
        }
        if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
        {
            project.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (files.Count > 0)
        {
            var root = await folders.RootAsync(cancellationToken);
            foreach (var file in files)
            {
                Voice.VoiceStage.DeleteQuietly(Path.Combine(root, file));   // once nothing names it
            }
        }
        return (keys, cell.State);
    }

    /// <summary>
    /// An image you set yourself (a picture used as it is) as the image's next version, to review:
    /// the references are always reviewed. Its cell is made when it has none yet.
    /// </summary>
    /// <returns>The image's state.</returns>
    public async Task<StageState> WriteImageAsync(Guid projectId, string key, ReferenceImage image, string inputHash, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == key, cancellationToken);
        if (cell is null)
        {
            cell = new CellEntry { ProjectId = projectId, Stage = Stage, Key = key, State = StageState.NotStarted, UpdatedAt = now };
            db.Cells.Add(cell);
        }
        var next = 1 + (await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == key).MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0);
        db.CellVersions.Add(new CellVersionEntry
        {
            ProjectId = projectId,
            Stage = Stage,
            Key = key,
            Version = next,
            Origin = VersionOrigin.Edited,
            BasedOn = cell.CurrentVersion,
            InputHash = inputHash,
            SchemaVersion = ReferenceSheet.SchemaVersion,
            OutputJson = StoredJson.Write(image),
            CreatedAt = now,
        });
        cell.CurrentVersion = next;
        cell.State = StageState.NeedsReview;
        cell.Error = null;
        cell.UpdatedAt = now;
        if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
        {
            project.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return cell.State;
    }

    /// <summary>A stage whose run failed goes back to review: what it made stays. True when it was failed.</summary>
    public async Task<bool> ReviewAgainAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        if (cell.State != StageState.Failed)
        {
            return false;
        }
        cell.State = StageState.NeedsReview;
        cell.Error = null;
        cell.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
