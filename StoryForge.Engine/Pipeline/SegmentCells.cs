using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Pipeline;

/// <summary>
/// What you do to the segments of a stage with a cell per segment (the script, the voice, the
/// storyboard's shots, the reference images, the stills): approve
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
    public static bool HasSegments(PipelineStage stage) =>
        stage is PipelineStage.Script or PipelineStage.Voice or PipelineStage.Storyboard or PipelineStage.References or PipelineStage.Stills or PipelineStage.Clips;

    public async Task ApproveAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken)
    {
        RequireIdle(projectId, stage, segmentId);
        await ChangeAsync(projectId, stage, segmentId, async (db, cell) =>
        {
            RequireVersion(db, cell, version);
            await RequireSameCutAsync(db, cell, version, cancellationToken);
            await RequireMadeAsync(db, cell, version, cancellationToken);
            cell.CurrentVersion = version;
            cell.ApprovedVersion = version;
            cell.State = StageState.Approved;
            cell.Error = null;
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
            if (stage == PipelineStage.References)
            {
                // A run that stopped before them: making the images is the fix, not another picture.
                var never = segments.Count(s => s.CurrentVersion is null);
                if (never > 0)
                {
                    throw new InvalidOperationException($"{never} {(never == 1 ? "image was" : "images were")} never made: make the images first.");
                }
                var without = new HashSet<string>();
                var made = new HashSet<string>();
                foreach (var segment in segments)
                {
                    var id = References.ReferencesStage.Parse(segment.Key).ReferenceId;
                    (await MadeAsync(db, segment, segment.CurrentVersion ?? 0, cancellationToken) ? made : without).Add(id);
                }
                // Another image of it was made: that one is the fix, not a new picture.
                if (without.Count > 0 && without.All(made.Contains))
                {
                    var names = await PipelineRunner.NamesAsync(db, projectId, without, cancellationToken);
                    throw new InvalidOperationException(
                        $"{string.Join(", ", names)} {(names.Count == 1 ? "has" : "have")} an image that shows nothing: remove it (Remove this image), then approve the rest.");
                }
                var nothing = without.Except(made).Count();
                if (nothing > 0)
                {
                    throw new InvalidOperationException(
                        $"Nothing was made for {nothing} of the references yet: search the sources again, add your own picture, or make it from the description.");
                }
                if (await PipelineRunner.WithoutImagesAsync(db, projectId, segments, cancellationToken) is { Count: > 0 } bare)
                {
                    throw new InvalidOperationException($"{string.Join(", ", bare)} {(bare.Count == 1 ? "has" : "have")} no image yet: make the images first.");
                }
            }
            // One never made (a clip that failed on its first try) has nothing to approve: it stays as it is.
            segments.RemoveAll(s => s.CurrentVersion is null);
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
        await ChangeAsync(projectId, stage, segmentId, async (db, cell) =>
        {
            RequireVersion(db, cell, version);
            await RequireSameCutAsync(db, cell, version, cancellationToken);
            cell.CurrentVersion = version;
            cell.ApprovedVersion = gated ? cell.ApprovedVersion : version;
            cell.State = version == cell.ApprovedVersion ? StageState.Approved : StageState.NeedsReview;
            cell.Error = null;
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

    /// <summary>
    /// A shot's version may only come back where it cuts as the shot does now: one that starts or
    /// ends elsewhere would overlap the shot next to it, or leave words no shot covers.
    /// </summary>
    private static async Task RequireSameCutAsync(StoryForgeDbContext db, CellEntry cell, int version, CancellationToken cancellationToken)
    {
        if (cell.Stage != PipelineStage.Storyboard || version == cell.CurrentVersion)
        {
            return;
        }
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == cell.ProjectId && v.Stage == cell.Stage && v.Key == cell.Key && (v.Version == version || v.Version == cell.CurrentVersion))
            .ToListAsync(cancellationToken);
        var wanted = StoredJson.Read<Shot>(versions.First(v => v.Version == version).OutputJson);
        var current = StoredJson.Read<Shot>(versions.First(v => v.Version == cell.CurrentVersion).OutputJson);
        if (wanted.FirstWord == current.FirstWord && wanted.WordCount == current.WordCount)
        {
            return;
        }
        var board = await db.Cells.AsNoTracking().Where(c => c.ProjectId == cell.ProjectId && c.Stage == cell.Stage && c.Key != "")
            .Join(db.CellVersions.AsNoTracking(), c => new { c.ProjectId, c.Stage, c.Key, Version = c.CurrentVersion ?? 0 }, v => new { v.ProjectId, v.Stage, v.Key, v.Version }, (c, v) => v.OutputJson)
            .ToListAsync(cancellationToken);
        var label = ShotTiming.Label([.. board.Select(StoredJson.Read<Shot>)], cell.Key);
        throw new InvalidOperationException($"v{version} of {label} cuts elsewhere: move the cut back instead, so the shots still add up to the audio.");
    }

    /// <summary>A reference image where nothing was made (no usable picture) cannot be approved.</summary>
    private static async Task RequireMadeAsync(StoryForgeDbContext db, CellEntry cell, int version, CancellationToken cancellationToken)
    {
        if (!await MadeAsync(db, cell, version, cancellationToken))
        {
            throw new InvalidOperationException("Nothing was made for this reference yet: search the sources again, add your own picture, or make it from the description.");
        }
    }

    private static async Task<bool> MadeAsync(StoryForgeDbContext db, CellEntry cell, int version, CancellationToken cancellationToken)
    {
        if (cell.Stage != PipelineStage.References)
        {
            return true;
        }
        var entry = await db.CellVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.ProjectId == cell.ProjectId && v.Stage == cell.Stage && v.Key == cell.Key && v.Version == version, cancellationToken);
        // No version yet (its first make failed or was cancelled): nothing was made either.
        return entry is not null && StoredJson.Read<ReferenceImage>(entry.OutputJson).Made != ImageMaking.NoPicture;
    }

    private static void RequireSegments(PipelineStage stage)
    {
        if (!HasSegments(stage))
        {
            throw new InvalidOperationException($"The {stage} stage has no segments.");
        }
    }

    private static string Name(PipelineStage stage) => stage switch
    {
        PipelineStage.Voice => "voice",
        PipelineStage.Storyboard => "storyboard",
        PipelineStage.References => "references",
        PipelineStage.Stills => "stills",
        PipelineStage.Clips => "clips",
        _ => "script",
    };

    private static string Doing(PipelineStage stage) => stage switch
    {
        PipelineStage.Voice => "spoken",
        PipelineStage.Storyboard => "planned",
        PipelineStage.References or PipelineStage.Stills or PipelineStage.Clips => "made",
        _ => "written",
    };
}
