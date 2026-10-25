using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;

namespace StoryForge.Engine.References;

/// <summary>
/// What you change on the References screen: the list (add, remove, merge, the look), the pictures
/// a reference is made from, your own pictures, and confirming the list so its images are made.
/// A change to the list is a new version of the stage's sheet; nothing changes while the stage runs.
/// </summary>
internal sealed class ReferenceEdits(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    CellReader cells,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.References;

    /// <summary>The list is confirmed: the sheet moves on to its images, and they are made.</summary>
    public async Task MakeImagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        RequireIdle(projectId);
        var sheet = await cells.CurrentReferencesAsync(projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} has no reference list yet.");
        if (sheet.References.Count == 0)
        {
            throw new InvalidOperationException("The list is empty: add a character, place or object first.");
        }
        if (sheet.Phase == ReferencePhase.List)
        {
            await SaveAsync(projectId, sheet with { Phase = ReferencePhase.Images }, cancellationToken);
        }
        await runner.RegenerateAsync(projectId, Stage, cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The References stage, or one of its images, is running.</exception>
    private void RequireIdle(Guid projectId)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The references are being made. Try again when they are done.");
        }
    }

    /// <summary>The changed sheet as the stage's next version (your edits of the one before).</summary>
    private async Task SaveAsync(Guid projectId, ReferenceSheet sheet, CancellationToken cancellationToken)
    {
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
        if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
        {
            project.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        runner.Publish(new StageUpdate(projectId, Stage, cell.State));
    }
}
