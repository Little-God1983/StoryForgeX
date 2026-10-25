using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.References;

/// <summary>
/// The References screen's content: the list with each reference's images and versions, the pool
/// with what each picture is used for, the rest of the cast. Approving and switching versions are
/// <see cref="SegmentCells"/>; changes are <see cref="ReferenceEdits"/>.
/// </summary>
internal sealed class ReferenceViews(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    CellReader cells,
    ProjectStore projects,
    ProjectFolders folders)
{
    private const PipelineStage Stage = PipelineStage.References;

    public async Task<ReferencesView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        var textOnly = setup.Stills.Consistency == Consistency.TextToImageOnly;
        var root = await folders.RootAsync(cancellationToken);
        var shots = await cells.CurrentShotsAsync(projectId, cancellationToken);
        var labels = shots.ToDictionary(s => s.Key, s => ShotTiming.Label(shots, s.Key));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The cells first, then their versions: a version is stored with (or before) the cell that
        // points to it, so a run storing meanwhile is seen whole or not at all.
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage).ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        var sheet = versions.FirstOrDefault(v => v.Key == "" && v.Version == whole?.CurrentVersion) is { } stored
            ? StoredJson.Read<ReferenceSheet>(stored.OutputJson)
            : null;
        if (whole is null)
        {
            return new ReferencesView(projectId, StageState.NotStarted, null, [], ReferencePhase.List, [], [], [], textOnly, labels);
        }

        var images = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            if (own.FirstOrDefault(v => v.Version == cell.CurrentVersion) is not { } current)
            {
                return null;
            }
            var image = StoredJson.Read<ReferenceImage>(current.OutputJson);
            return new ReferenceImageView(
                cell.Key,
                image.Number,
                cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                image,
                Existing(root, image.File),
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson));
        }).OfType<ReferenceImageView>().ToList();

        var references = (sheet?.References ?? []).Select(r => new ReferenceView(
            r,
            [.. images.Where(i => i.Image.ReferenceId == r.Id).OrderBy(i => i.Number)])).ToList();
        var pool = (sheet?.Pool ?? []).Select(p => new PoolImageView(
            p,
            Existing(root, p.File),
            [.. references.Where(r => r.Reference.Sources.Contains(p.Id)).Select(r => r.Reference.Name)])).ToList();
        return new ReferencesView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson),
            sheet?.Phase ?? ReferencePhase.List,
            references,
            pool,
            sheet?.Others ?? [],
            textOnly,
            labels);
    }

    private static string? Existing(string root, string? file) =>
        file is not null && File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file) : null;
}
