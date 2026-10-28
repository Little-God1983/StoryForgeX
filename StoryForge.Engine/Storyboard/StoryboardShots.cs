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
    ProfileStore profiles,
    Staleness staleness)
{
    private const PipelineStage Stage = PipelineStage.Storyboard;

    public async Task<StoryboardView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The cells first, then their versions: a version is stored with (or before) the cell that
        // points to it, so a run storing the storyboard meanwhile is seen whole or not at all.
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage).ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        var sheet = versions.FirstOrDefault(v => v.Key == "" && v.Version == whole?.CurrentVersion) is { } stored
            ? StoredJson.Read<StoryboardSheet>(stored.OutputJson)
            : null;
        var pacing = sheet?.Pacing ?? await PacingAsync(setup, cancellationToken);
        var words = new Dictionary<string, IReadOnlyList<SpokenWord>>();
        if (whole is null)
        {
            return new StoryboardView(projectId, StageState.NotStarted, null, [], [], [], words, pacing, setup.Clips.MaxClipSeconds);
        }
        foreach (var (segmentId, version) in sheet?.VoiceVersions ?? new Dictionary<string, int>())
        {
            words[segmentId] = (await cells.VoiceAsync(projectId, segmentId, version, cancellationToken))?.Words ?? [];
        }

        versions.RemoveAll(v => v.Key == "");
        var stale = await staleness.GetAsync(projectId, cancellationToken);
        // While a rerun cuts some segments again, their shots are being made: the others stay as they are.
        var recut = runner.Scope(projectId, Stage) ?? new HashSet<string>();
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
            recut.Contains(s.Shot.SegmentId) ? StageState.Running : s.Cell.State,
            s.Cell.CurrentVersion ?? 0,
            s.Cell.ApprovedVersion,
            [.. s.Own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
            s.Shot,
            s.Cell.State == StageState.Failed ? s.Cell.Error : null,
            runner.LiveActivity(projectId, Stage, s.Cell.Key) ?? StoredJson.Read<List<ActivityLine>>(s.Cell.ActivityJson))
        {
            MovedVersions = [.. s.Own.Select(v => (v.Version, Shot: StoredJson.Read<Shot>(v.OutputJson)))
                .Where(v => v.Shot.FirstWord != s.Shot.FirstWord || v.Shot.WordCount != s.Shot.WordCount)
                .Select(v => v.Version)],
            Stale = stale.GetValueOrDefault((Stage, s.Cell.Key)),
            Rerun = s.Cell.Rerun,
        }).ToList();
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
        ?? (await profiles.GetVersionAsync(setup.Script.Storyboard.ProfileId, setup.Script.Storyboard.Version, cancellationToken)).Content.Pacing
        ?? Pacing.Default;
}
