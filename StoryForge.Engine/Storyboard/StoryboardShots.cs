using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Storyboard;

/// <summary>
/// The storyboard as the result matrix shows it: its shots in order, numbered by their place in
/// their segment, and the words they were cut from. Approving and switching versions are
/// <see cref="SegmentCells"/>; changing shots is <see cref="StoryboardEdits"/>.
/// </summary>
internal sealed class StoryboardShots(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    CellReader cells,
    ProjectStore projects,
    ProfileStore profiles)
{
    private const PipelineStage Stage = PipelineStage.Storyboard;

    public async Task<StoryboardView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        var sheet = await cells.CurrentStoryboardAsync(projectId, cancellationToken);
        var pacing = sheet?.Pacing ?? await PacingAsync(setup, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key != "").ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        var words = new Dictionary<string, IReadOnlyList<SpokenWord>>();
        if (whole is null)
        {
            return new StoryboardView(projectId, StageState.NotStarted, null, [], [], [], words, pacing, setup.Clips.MaxClipSeconds);
        }
        foreach (var (segmentId, version) in sheet?.VoiceVersions ?? new Dictionary<string, int>())
        {
            words[segmentId] = (await cells.VoiceAsync(projectId, segmentId, version, cancellationToken))?.Words ?? [];
        }

        var shown = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            return own.FirstOrDefault(v => v.Version == cell.CurrentVersion) is { } current
                ? (Cell: cell, Own: own, Shot: StoredJson.Read<Shot>(current.OutputJson))
                : default;
        }).Where(s => s.Cell is not null).OrderBy(s => s.Shot.SegmentId, StringComparer.Ordinal).ThenBy(s => s.Shot.FirstWord).ToList();
        var shots = shown.Select(s => new ShotView(
            s.Cell.Key,
            s.Shot.SegmentId,
            1 + shown.Count(o => o.Shot.SegmentId == s.Shot.SegmentId && o.Shot.FirstWord < s.Shot.FirstWord),
            s.Cell.State,
            s.Cell.CurrentVersion ?? 0,
            s.Cell.ApprovedVersion,
            [.. s.Own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
            s.Shot,
            s.Cell.State == StageState.Failed ? s.Cell.Error : null,
            runner.LiveActivity(projectId, Stage, s.Cell.Key) ?? StoredJson.Read<List<ActivityLine>>(s.Cell.ActivityJson))).ToList();
        return new StoryboardView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson),
            shots,
            sheet?.Cast ?? [],
            words,
            pacing,
            setup.Clips.MaxClipSeconds);
    }

    /// <summary>The pacing a storyboard planned now would use: the project's own, else its storyboard profile's.</summary>
    private async Task<Pacing> PacingAsync(ProjectSetup setup, CancellationToken cancellationToken) =>
        setup.Pacing
        ?? (await profiles.GetVersionAsync(setup.Writing.Storyboard.ProfileId, setup.Writing.Storyboard.Version, cancellationToken)).Content.Pacing
        ?? Pacing.Default;
}
