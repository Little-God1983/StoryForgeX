using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Script;

/// <summary>
/// The script as the result matrix shows it, and what you do to its segments: approve one or all,
/// reword one (a new version), switch one back to an older version. Each change settles the stage:
/// approved once every segment is, and then the run goes on.
/// </summary>
internal sealed class ScriptSegments(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
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

        var segments = stageCells.Where(c => c.Key != "").OrderBy(c => c.Key, StringComparer.Ordinal).Select(cell =>
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
        var seconds = segments.Sum(s => s.Seconds);
        return new ScriptView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            activity,
            segments,
            // Why it is longer than the target: said of the script as written, gone once the segments fit.
            seconds > setup.Output.TargetSeconds ? script?.LengthNote ?? "" : "",
            seconds,
            setup.Output.TargetSeconds,
            facts);
    }

    public async Task ApproveAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken)
    {
        RequireIdle(projectId, segmentId);
        await ChangeAsync(projectId, segmentId, (db, cell) =>
        {
            RequireVersion(db, cell, version);
            cell.CurrentVersion = version;
            cell.ApprovedVersion = version;
            cell.State = StageState.Approved;
            cell.Error = null;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>"Approve remaining": every segment as it stands.</summary>
    public async Task ApproveAllAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The script is being written. Approve it when it is done.");
        }
        List<string> approved;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var segments = await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == Stage && c.Key != "").ToListAsync(cancellationToken);
            if (segments.Count == 0)
            {
                throw new KeyNotFoundException($"Project {projectId} has no script yet.");
            }
            approved = [.. segments.Where(s => s.State != StageState.Approved).Select(s => s.Key)];
            foreach (var segment in segments)
            {
                segment.ApprovedVersion = segment.CurrentVersion;
                segment.State = StageState.Approved;
                segment.Error = null;
                segment.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        foreach (var key in approved)
        {
            runner.Publish(new StageUpdate(projectId, Stage, StageState.Approved, Key: key));
        }
        await runner.SettleAsync(projectId, Stage, cancellationToken);
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
        RequireIdle(projectId, segmentId);
        var gated = await GatedAsync(projectId, cancellationToken);
        await ChangeAsync(projectId, segmentId, async (db, cell) =>
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

    /// <summary>
    /// Makes another version current again: approved if it is the approved one, else to review.
    /// Without the Script gate it is approved as it is.
    /// </summary>
    public async Task SelectAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken)
    {
        RequireIdle(projectId, segmentId);
        var gated = await GatedAsync(projectId, cancellationToken);
        await ChangeAsync(projectId, segmentId, (db, cell) =>
        {
            RequireVersion(db, cell, version);
            cell.CurrentVersion = version;
            cell.ApprovedVersion = gated ? cell.ApprovedVersion : version;
            cell.State = version == cell.ApprovedVersion ? StageState.Approved : StageState.NeedsReview;
            cell.Error = null;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task<bool> GatedAsync(Guid projectId, CancellationToken cancellationToken) =>
        PipelineRunner.IsGated((await projects.GetAsync(projectId, cancellationToken)).Setup, Stage);

    private void RequireIdle(Guid projectId, string segmentId)
    {
        if (runner.IsBusy(projectId, Stage) || runner.IsBusy(projectId, Stage, segmentId))
        {
            throw new InvalidOperationException($"{segmentId} is being written. Try again when it is done.");
        }
    }

    private static void RequireVersion(StoryForgeDbContext db, CellEntry cell, int version)
    {
        if (!db.CellVersions.Any(v => v.ProjectId == cell.ProjectId && v.Stage == Stage && v.Key == cell.Key && v.Version == version))
        {
            throw new KeyNotFoundException($"{cell.Key} has no version {version}.");
        }
    }

    private async Task ChangeAsync(Guid projectId, string segmentId, Func<StoryForgeDbContext, CellEntry, Task> change, CancellationToken cancellationToken)
    {
        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == segmentId, cancellationToken)
                ?? throw new KeyNotFoundException($"The script has no segment {segmentId}.");
            await change(db, cell);
            cell.UpdatedAt = clock.GetUtcNow();
            if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
            {
                project.UpdatedAt = cell.UpdatedAt;
            }
            await db.SaveChangesAsync(cancellationToken);
            state = cell.State;
        }
        runner.Publish(new StageUpdate(projectId, Stage, state, Key: segmentId));
        await runner.SettleAsync(projectId, Stage, cancellationToken);
    }
}
