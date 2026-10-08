using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Voice;

/// <summary>
/// The voice as the result matrix shows it: each segment's audio, its words and versions, and the
/// voice total. Approving and switching versions are <see cref="SegmentCells"/>, shared with the script.
/// </summary>
internal sealed class VoiceSegments(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    ProjectStore projects,
    ProjectFolders folders)
{
    private const PipelineStage Stage = PipelineStage.Voice;

    public async Task<VoiceView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        var root = await folders.RootAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage).ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        if (whole is null)
        {
            return new VoiceView(projectId, StageState.NotStarted, null, [], [], 0, setup.Output.TargetSeconds);
        }

        // While the whole voice is spoken, a segment this run has not reached yet is still to come.
        var stored = runner.StoredInRun(projectId, Stage);
        var segments = stageCells.Where(c => c.Key != "").OrderBy(c => c.Key, StringComparer.Ordinal).Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            if (own.FirstOrDefault(v => v.Version == cell.CurrentVersion) is not { } current)
            {
                return null;   // never spoken: nothing to show
            }
            var clip = StoredJson.Read<VoiceClip>(current.OutputJson);
            var path = Path.Combine(root, clip.AudioFile);
            return new VoiceSegmentView(
                cell.Key,
                stored is not null && !stored.Contains(cell.Key) ? StageState.Running : cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                File.Exists(path) ? path : null,
                clip.Seconds,
                clip.Parts,
                clip.Words,
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson));
        }).OfType<VoiceSegmentView>().ToList();
        return new VoiceView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson),
            segments,
            Math.Round(segments.Sum(s => s.Seconds), 3),
            setup.Output.TargetSeconds);
    }
}
