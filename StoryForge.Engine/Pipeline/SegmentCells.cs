using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Pipeline;

/// <summary>
/// What you do to the segments of a stage with a cell per segment (the script, the voice): approve
/// one or all, switch one back to an older version. Each change settles the stage: approved once
/// every segment is, and then the run goes on.
/// </summary>
internal sealed class SegmentCells(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    ProjectStore projects,
    TimeProvider clock)
{
    /// <summary>The stages whose segments are cells of their own.</summary>
    public static bool HasSegments(PipelineStage stage) => stage is PipelineStage.Script or PipelineStage.Voice;

    public async Task ApproveAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken)
    {
        RequireIdle(projectId, stage, segmentId);
        await ChangeAsync(projectId, stage, segmentId, (db, cell) =>
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
    public async Task ApproveAllAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        RequireSegments(stage);
        if (runner.IsAnyBusy(projectId, stage))
        {
            throw new InvalidOperationException($"The {Name(stage)} is being {Doing(stage)}. Approve it when it is done.");
        }
        List<string> approved;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var segments = await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key != "").ToListAsync(cancellationToken);
            if (segments.Count == 0)
            {
                throw new KeyNotFoundException($"Project {projectId} has no {Name(stage)} yet.");
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
            runner.Publish(new StageUpdate(projectId, stage, StageState.Approved, Key: key));
        }
        await runner.SettleAsync(projectId, stage, cancellationToken);
    }

    /// <summary>
    /// Makes another version current again: approved if it is the approved one, else to review.
    /// Without the stage's gate it is approved as it is.
    /// </summary>
    public async Task SelectAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken)
    {
        RequireIdle(projectId, stage, segmentId);
        var gated = await GatedAsync(projectId, stage, cancellationToken);
        await ChangeAsync(projectId, stage, segmentId, (db, cell) =>
        {
            RequireVersion(db, cell, version);
            cell.CurrentVersion = version;
            cell.ApprovedVersion = gated ? cell.ApprovedVersion : version;
            cell.State = version == cell.ApprovedVersion ? StageState.Approved : StageState.NeedsReview;
            cell.Error = null;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<bool> GatedAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken) =>
        PipelineRunner.IsGated((await projects.GetAsync(projectId, cancellationToken)).Setup, stage);

    /// <exception cref="InvalidOperationException">The segment or its whole stage is running.</exception>
    public void RequireIdle(Guid projectId, PipelineStage stage, string segmentId)
    {
        RequireSegments(stage);
        if (runner.IsBusy(projectId, stage) || runner.IsBusy(projectId, stage, segmentId))
        {
            throw new InvalidOperationException($"{segmentId} is being {Doing(stage)}. Try again when it is done.");
        }
    }

    /// <summary>Changes one segment's cell, then tells the app and settles the stage.</summary>
    public async Task ChangeAsync(Guid projectId, PipelineStage stage, string segmentId, Func<StoryForgeDbContext, CellEntry, Task> change, CancellationToken cancellationToken)
    {
        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == segmentId, cancellationToken)
                ?? throw new KeyNotFoundException($"The {Name(stage)} has no segment {segmentId}.");
            await change(db, cell);
            cell.UpdatedAt = clock.GetUtcNow();
            if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
            {
                project.UpdatedAt = cell.UpdatedAt;
            }
            await db.SaveChangesAsync(cancellationToken);
            state = cell.State;
        }
        runner.Publish(new StageUpdate(projectId, stage, state, Key: segmentId));
        await runner.SettleAsync(projectId, stage, cancellationToken);
    }

    private static void RequireVersion(StoryForgeDbContext db, CellEntry cell, int version)
    {
        if (!db.CellVersions.Any(v => v.ProjectId == cell.ProjectId && v.Stage == cell.Stage && v.Key == cell.Key && v.Version == version))
        {
            throw new KeyNotFoundException($"{cell.Key} has no version {version}.");
        }
    }

    private static void RequireSegments(PipelineStage stage)
    {
        if (!HasSegments(stage))
        {
            throw new InvalidOperationException($"The {stage} stage has no segments.");
        }
    }

    private static string Name(PipelineStage stage) => stage == PipelineStage.Voice ? "voice" : "script";

    private static string Doing(PipelineStage stage) => stage == PipelineStage.Voice ? "spoken" : "written";
}
