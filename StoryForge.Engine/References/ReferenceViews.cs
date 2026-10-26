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
        var storyboardApproved = await cells.StateAsync(projectId, PipelineStage.Storyboard, cancellationToken) == StageState.Approved;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The cells first, then their versions: a version is stored with (or before) the cell that
        // points to it, so a run storing meanwhile is seen whole or not at all.
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        // Every version's dates, but the stored output of the current ones only: each list edit keeps a whole sheet.
        var versions = (await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == Stage)
            .Select(v => new { v.Key, v.Version, v.Origin, v.CreatedAt, v.BasedOn })
            .ToListAsync(cancellationToken)).ToLookup(v => v.Key);
        var current = stageCells.Where(c => c.CurrentVersion is not null).ToDictionary(c => c.Key, c => c.CurrentVersion!.Value);
        var keys = current.Keys.ToList();
        var numbers = current.Values.Distinct().ToList();
        var outputs = (await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == Stage && keys.Contains(v.Key) && numbers.Contains(v.Version))
            .Select(v => new { v.Key, v.Version, v.OutputJson })
            .ToListAsync(cancellationToken))
            .Where(v => current[v.Key] == v.Version)
            .ToDictionary(v => v.Key, v => v.OutputJson);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        var sheet = outputs.TryGetValue("", out var stored) ? StoredJson.Read<ReferenceSheet>(stored) : null;
        if (whole is null)
        {
            return new ReferencesView(projectId, StageState.NotStarted, null, [], ReferencePhase.List, [], [], [], textOnly, labels, 0, storyboardApproved);
        }

        var images = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions[cell.Key].OrderBy(v => v.Version).ToList();
            // An image being made for the first time (or failed at it) has no version yet: it shows by its state.
            var (referenceId, number) = ReferencesStage.Parse(cell.Key);
            var image = outputs.TryGetValue(cell.Key, out var json)
                ? StoredJson.Read<ReferenceImage>(json)
                : new ReferenceImage(referenceId, number, null, [], 0, ImageMaking.FromPicture, "");
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
        }).ToList();

        // Who and what is in a shot is the shot's: the timeline and the Board panel change the same.
        var references = (sheet?.References ?? []).Select(r => new ReferenceView(
            r with { Shots = ReferenceList.ShotsOf(r, shots) },
            [.. images.Where(i => i.Image.ReferenceId == r.Id).OrderBy(i => i.Number)])).ToList();
        var others = (sheet?.Others ?? []).Select(o => o with { Shots = shots.Count(s => s.Cast.Any(n => ReferenceList.Same(n, o.Name))) }).ToList();
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
            others,
            textOnly,
            labels,
            whole.CurrentVersion ?? 0,
            storyboardApproved) { Shots = Timeline(shots, labels) };
    }

    /// <summary>The shots one after another, as the video plays them: each segment's shots add up to its audio.</summary>
    private static IReadOnlyList<TimelineShot> Timeline(IReadOnlyList<Shot> shots, IReadOnlyDictionary<string, string> labels)
    {
        var start = 0.0;
        var timeline = new List<TimelineShot>();
        foreach (var shot in shots)
        {
            timeline.Add(new TimelineShot(shot.Key, labels[shot.Key], start, shot.Seconds, shot.Cast));
            start += shot.Seconds;
        }
        return timeline;
    }

    private static string? Existing(string root, string? file) =>
        file is not null && File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file) : null;
}
