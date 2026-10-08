using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Script;

/// <summary>
/// The script as the result matrix shows it, and rewording a segment (a new version). Approving
/// and switching versions are <see cref="SegmentCells"/>, shared with the voice.
/// </summary>
internal sealed class ScriptSegments(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    CellReader cells,
    ProjectStore projects,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Script;

    public async Task<ScriptView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        var script = await cells.CurrentScriptAsync(projectId, cancellationToken);
        // The sheet the script was written from: its segments name that sheet's facts.
        var facts = (script is { FactsVersion: > 0 } ? await cells.FactSheetAsync(projectId, script.FactsVersion, cancellationToken) : null)?.Facts
            ?? (await cells.ApprovedFactSheetAsync(projectId, cancellationToken))?.Sheet.Facts
            ?? [];
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage).ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        if (whole is null)
        {
            return new ScriptView(projectId, StageState.NotStarted, null, [], [], "", 0, setup.Output.TargetSeconds, facts);
        }

        var shownSegments = stageCells.Where(c => c.Key != "").OrderBy(c => c.Key, StringComparer.Ordinal).Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            if (own.FirstOrDefault(v => v.Version == cell.CurrentVersion) is not { } current)
            {
                return null;   // never written: nothing to show
            }
            var shown = StoredJson.Read<Segment>(current.OutputJson);
            return new SegmentView(
                cell.Key,
                shown.Title,
                cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                shown.Narration,
                shown.FactIds,
                ScriptSheet.Seconds(shown.Narration, setup.Output.Language),
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson));
        }).OfType<SegmentView>().ToList();
        var activity = runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson);
        var seconds = shownSegments.Sum(s => s.Seconds);
        return new ScriptView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            activity,
            shownSegments,
            // Why it is longer than the target: said of the script as written, gone once the segments fit.
            seconds > setup.Output.TargetSeconds ? script?.LengthNote ?? "" : "",
            seconds,
            setup.Output.TargetSeconds,
            facts);
    }

    /// <summary>
    /// Your wording, saved as the segment's next version; it keeps the facts the segment listed.
    /// Without the Script gate it is approved as it is, like a segment written again.
    /// </summary>
    public async Task EditAsync(Guid projectId, string segmentId, string title, string narration, CancellationToken cancellationToken)
    {
        title = (title ?? "").Trim();
        narration = (narration ?? "").Trim();
        if (title.Length == 0 || narration.Length == 0)
        {
            throw new ArgumentException("A segment needs a title and a narration.");
        }
        segments.RequireIdle(projectId, Stage, segmentId);
        var gated = await segments.GatedAsync(projectId, Stage, cancellationToken);
        await segments.ChangeAsync(projectId, Stage, segmentId, async (db, cell) =>
        {
            var versions = await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == segmentId).ToListAsync(cancellationToken);
            var current = versions.First(v => v.Version == cell.CurrentVersion);
            var basis = StoredJson.Read<Segment>(current.OutputJson);
            if (basis.Title == title && basis.Narration == narration)
            {
                return;
            }
            var next = versions.Max(v => v.Version) + 1;
            db.CellVersions.Add(new CellVersionEntry
            {
                ProjectId = projectId,
                Stage = Stage,
                Key = segmentId,
                Version = next,
                Origin = VersionOrigin.Edited,
                BasedOn = current.Version,
                InputHash = current.InputHash,
                SchemaVersion = ScriptSheet.SchemaVersion,
                OutputJson = StoredJson.Write(basis with { Title = title, Narration = narration }),
                CreatedAt = clock.GetUtcNow(),
            });
            cell.CurrentVersion = next;
            cell.State = gated ? StageState.NeedsReview : StageState.Approved;
            cell.ApprovedVersion = gated ? cell.ApprovedVersion : next;
            cell.Error = null;
        }, cancellationToken);
    }
}
