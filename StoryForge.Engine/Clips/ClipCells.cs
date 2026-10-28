using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Clips;

/// <summary>
/// The clips as the matrix shows them, and what you do to them besides approving and switching
/// versions (<see cref="SegmentCells"/>): Regenerate, Edit prompt… and Make with… (another video
/// profile, the fallback) put a clip among the waiting ones; Make clips makes them (and any shot's
/// clip still missing) in one run.
/// </summary>
internal sealed class ClipCells(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    CellReader cells,
    ProjectStore projects,
    ProfileStore profiles,
    ProjectFolders folders,
    Staleness staleness,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Clips;

    public async Task<ClipsView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var root = await folders.RootAsync(cancellationToken);
        var stillsApproved = await cells.StateAsync(projectId, PipelineStage.Stills, cancellationToken) == StageState.Approved;
        var others = (await profiles.ListAsync(cancellationToken))
            .Where(p => p.Kind == ProfileKind.Video && p.Id != project.Setup.Clips.Profile.ProfileId)
            .Select(p => new ClipModel(new ProfileRef(p.Id, p.LatestVersion), p.Name))
            .ToList();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The cells first, then their versions: a version is stored with (or before) the cell that points to it.
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key != "").ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        if (whole is null)
        {
            return new ClipsView(projectId, StageState.NotStarted, null, [], [], stillsApproved, project.Setup.Clips.Model, others);
        }
        // While the clips are made, one this run is to make (waiting, or never made) and has not reached yet is still to come.
        var stored = runner.StoredInRun(projectId, Stage);
        var stale = await staleness.GetAsync(projectId, cancellationToken);
        var clips = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            var current = own.FirstOrDefault(v => v.Version == cell.CurrentVersion);
            var clip = current is null ? null : StoredJson.Read<Clip>(current.OutputJson);
            var path = clip is null ? null : Path.Combine(root, clip.File);
            return new ClipView(
                cell.Key,
                stored is not null && !stored.Contains(cell.Key) && (cell.Queued || cell.CurrentVersion is null) ? StageState.Running : cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                clip,
                path is not null && File.Exists(path) ? path : null,
                current?.InputHash ?? "",
                cell.Queued,
                cell.QueuedPrompt,
                cell.QueuedProfile is null ? null : StoredJson.Read<ProfileRef>(cell.QueuedProfile),
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson))
            {
                Stale = stale.GetValueOrDefault((Stage, cell.Key)),
            };
        }).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        return new ClipsView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson),
            clips,
            stillsApproved,
            project.Setup.Clips.Model,
            others);
    }

    /// <summary>Regenerate (or Edit prompt…, with your prompt; or Make with…, with another video profile): the clip waits for Make clips.</summary>
    /// <param name="prompt">
    /// Your prompt, used as it is; null or blank for one Claude writes. Regenerate on a clip already
    /// waiting with your prompt keeps it: take it out to have Claude write it again.
    /// </param>
    /// <param name="profile">Another video profile to make it with; null keeps the one it waits with (the project's when none).</param>
    public async Task QueueAsync(Guid projectId, string shotKey, string? prompt, ProfileRef? profile, CancellationToken cancellationToken)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        segments.RequireIdle(projectId, Stage, shotKey);
        if (profile is not null)
        {
            var project = await projects.GetAsync(projectId, cancellationToken);
            if (profile.ProfileId != project.Setup.Clips.Profile.ProfileId
                && (await profiles.ListAsync(cancellationToken)).All(p => p.Id != profile.ProfileId || p.Kind != ProfileKind.Video))
            {
                throw new KeyNotFoundException("There is no such video profile.");
            }
            await profiles.GetVersionAsync(profile.ProfileId, profile.Version, cancellationToken);   // the version exists
            if (profile == project.Setup.Clips.Profile)
            {
                profile = null;   // back to the project's own
            }
        }
        await ChangeAsync(projectId, shotKey, cell =>
        {
            cell.QueuedPrompt = prompt ?? (cell.Queued ? cell.QueuedPrompt : null);
            cell.QueuedProfile = profile is not null ? StoredJson.Write(profile) : cell.Queued ? cell.QueuedProfile : null;
            cell.Queued = true;
        }, cancellationToken);
    }

    /// <summary>Takes the clip out of the waiting ones: it stays as it is.</summary>
    public async Task UnqueueAsync(Guid projectId, string shotKey, CancellationToken cancellationToken)
    {
        segments.RequireIdle(projectId, Stage, shotKey);
        await ChangeAsync(projectId, shotKey, cell =>
        {
            cell.Queued = false;
            cell.QueuedPrompt = null;
            cell.QueuedProfile = null;
        }, cancellationToken);
    }

    /// <summary>
    /// Make clips: one run makes what is waiting, each model's together: every clip queued again, and
    /// every shot with no clip yet. The first time, that is all of them.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is waiting, the stills are not approved, or the clips are being made.</exception>
    public async Task MakeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The clips are being made. Try again when they are done.");
        }
        await projects.GetAsync(projectId, cancellationToken);
        if (await cells.StateAsync(projectId, PipelineStage.Stills, cancellationToken) != StageState.Approved)
        {
            throw new InvalidOperationException("Approve every still first: the clips are made from them.");
        }
        var made = await cells.CurrentClipsAsync(projectId, cancellationToken);
        var missing = (await cells.CurrentShotsAsync(projectId, cancellationToken)).Any(s => !made.ContainsKey(s.Key));
        if (!missing && (await cells.QueuedClipsAsync(projectId, cancellationToken)).Count == 0)
        {
            if (await ReviewAgainAsync(projectId, cancellationToken))
            {
                return;   // a run cut off after its last clip: every clip is there, to review
            }
            throw new InvalidOperationException("No clip is waiting to be made again: click Regenerate on the ones you want first.");
        }
        await runner.RegenerateAsync(projectId, Stage, cancellationToken);
    }

    /// <summary>A failed stage with every clip made goes back to review, and settles from its clips. True when it was failed.</summary>
    private async Task<bool> ReviewAgainAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var whole = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
            if (whole is not { State: StageState.Failed })
            {
                return false;
            }
            whole.State = StageState.NeedsReview;
            whole.Error = null;
            whole.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        runner.Publish(new StageUpdate(projectId, Stage, StageState.NeedsReview));
        await runner.SettleAsync(projectId, Stage, cancellationToken);
        return true;
    }

    private async Task ChangeAsync(Guid projectId, string shotKey, Action<CellEntry> change, CancellationToken cancellationToken)
    {
        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == shotKey, cancellationToken)
                ?? throw new KeyNotFoundException($"There is no clip of {shotKey} yet.");
            change(cell);
            cell.UpdatedAt = clock.GetUtcNow();
            if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
            {
                project.UpdatedAt = cell.UpdatedAt;
            }
            await db.SaveChangesAsync(cancellationToken);
            state = cell.State;
        }
        runner.Publish(new StageUpdate(projectId, Stage, state, Key: shotKey));
    }
}
