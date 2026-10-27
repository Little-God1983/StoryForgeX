using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;

namespace StoryForge.Engine.Stills;

/// <summary>
/// The stills as the matrix shows them, and what you do to them besides approving and switching
/// versions (<see cref="SegmentCells"/>): Regenerate and Edit prompt… put a still in the batch,
/// Continue pipeline makes the batch (and any shot's still still missing) in one run, Replace with
/// file… puts your own picture in.
/// </summary>
internal sealed class StillCells(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    CellReader cells,
    ProjectStore projects,
    ProjectFolders folders,
    Staleness staleness,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Stills;

    public async Task<StillsView> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        var root = await folders.RootAsync(cancellationToken);
        var referencesApproved = await cells.StateAsync(projectId, PipelineStage.References, cancellationToken) == StageState.Approved;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The cells first, then their versions: a version is stored with (or before) the cell that points to it.
        var stageCells = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key != "").ToListAsync(cancellationToken);
        var whole = stageCells.FirstOrDefault(c => c.Key == "");
        if (whole is null)
        {
            return new StillsView(projectId, StageState.NotStarted, null, [], [], referencesApproved);
        }
        // While the stills are made, one this run is to make (in the batch, or never made) and has not reached yet is still to come.
        var stored = runner.StoredInRun(projectId, Stage);
        var stale = await staleness.GetAsync(projectId, cancellationToken);
        var stills = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            var current = own.FirstOrDefault(v => v.Version == cell.CurrentVersion);
            var still = current is null ? null : StoredJson.Read<Still>(current.OutputJson);
            var path = still is null ? null : Path.Combine(root, still.File);
            return new StillView(
                cell.Key,
                stored is not null && !stored.Contains(cell.Key) && (cell.Queued || cell.CurrentVersion is null) ? StageState.Running : cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                still,
                path is not null && File.Exists(path) ? path : null,
                current?.InputHash ?? "",
                cell.Queued,
                cell.QueuedPrompt,
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson))
            {
                Stale = stale.GetValueOrDefault((Stage, cell.Key)),
            };
        }).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        return new StillsView(
            projectId,
            whole.State,
            whole.State == StageState.Failed ? whole.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(whole.ActivityJson),
            stills,
            referencesApproved);
    }

    /// <summary>Regenerate (or Edit prompt…, with your prompt): the still waits in the batch for Continue pipeline.</summary>
    /// <param name="prompt">
    /// Your prompt, used as it is; null or blank for one Claude writes. Regenerate on a still already
    /// waiting with your prompt keeps it: take it out of the batch to have Claude write it again.
    /// </param>
    public async Task QueueAsync(Guid projectId, string shotKey, string? prompt, CancellationToken cancellationToken)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        segments.RequireIdle(projectId, Stage, shotKey);
        await ChangeAsync(projectId, shotKey, cell =>
        {
            cell.QueuedPrompt = prompt ?? (cell.Queued ? cell.QueuedPrompt : null);
            cell.Queued = true;
        }, cancellationToken);
    }

    /// <summary>Takes the still out of the batch: it stays as it is.</summary>
    public async Task UnqueueAsync(Guid projectId, string shotKey, CancellationToken cancellationToken)
    {
        segments.RequireIdle(projectId, Stage, shotKey);
        await ChangeAsync(projectId, shotKey, cell =>
        {
            cell.Queued = false;
            cell.QueuedPrompt = null;
        }, cancellationToken);
    }

    /// <summary>
    /// Continue pipeline: one run makes what is waiting, in the order of the shots, so the model loads
    /// once: every still in the batch, and every shot with no still yet (never made, a run that
    /// failed before it, a shot split off later). The first time, that is all of them.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is waiting, the references are not approved, or the stills are being made.</exception>
    public async Task ContinueAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The stills are being made. Try again when they are done.");
        }
        await projects.GetAsync(projectId, cancellationToken);
        if (await cells.StateAsync(projectId, PipelineStage.References, cancellationToken) != StageState.Approved)
        {
            throw new InvalidOperationException("Approve the references first: the stills are made from them.");
        }
        var made = await cells.CurrentStillsAsync(projectId, cancellationToken);
        var missing = (await cells.CurrentShotsAsync(projectId, cancellationToken)).Any(s => !made.ContainsKey(s.Key));
        if (!missing && (await cells.QueuedStillsAsync(projectId, cancellationToken)).Count == 0)
        {
            if (await ReviewAgainAsync(projectId, cancellationToken))
            {
                return;   // a run cut off after its last still: every still is there, to review
            }
            throw new InvalidOperationException("No still is waiting to be made again: click Regenerate on the ones you want first.");
        }
        await runner.RegenerateAsync(projectId, Stage, cancellationToken);
    }

    /// <summary>A failed stage with every still made goes back to review, and settles from its stills. True when it was failed.</summary>
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

    /// <summary>Your own picture as the still's next version, to review like any other; nothing is made.</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ArgumentException">The file is not a PNG, JPEG or WebP picture.</exception>
    public async Task ReplaceAsync(Guid projectId, string shotKey, string path, CancellationToken cancellationToken)
    {
        segments.RequireIdle(projectId, Stage, shotKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file {path}.", path);
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var ending = ImageSize.Ending(bytes);
        if (ending is null || ImageSize.Of(bytes) is not { } size)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is not a PNG, JPEG or WebP picture.", nameof(path));
        }
        var project = await projects.GetAsync(projectId, cancellationToken);
        var wanted = project.Setup.Stills.Size;
        if (Math.Abs((double)size.Width / size.Height / ((double)wanted.Width / wanted.Height) - 1) > 0.02)
        {
            throw new ArgumentException(
                $"{Path.GetFileName(path)} is {size.Width}×{size.Height}; the stills of this project are {wanted.Aspect}. Pick a {wanted.Aspect} picture.", nameof(path));
        }
        if ((await cells.CurrentShotsAsync(projectId, cancellationToken)).All(s => s.Key != shotKey))
        {
            throw new KeyNotFoundException($"The storyboard has no shot {shotKey}.");
        }
        var gated = PipelineRunner.IsGated(project.Setup, Stage);
        var root = await folders.RootAsync(cancellationToken);
        var file = Path.Combine(ProjectFolders.Of(project), "stills", $"{StillsStage.FileName(shotKey)}-{Guid.NewGuid().ToString("N")[..8]}{ending}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
        await File.WriteAllBytesAsync(Path.Combine(root, file), bytes, cancellationToken);

        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = clock.GetUtcNow();
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == shotKey, cancellationToken);
            if (cell is null)
            {
                cell = new CellEntry { ProjectId = projectId, Stage = Stage, Key = shotKey, State = StageState.NotStarted, UpdatedAt = now };
                db.Cells.Add(cell);
            }
            var current = cell.CurrentVersion is { } at
                ? await db.CellVersions.FirstOrDefaultAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == shotKey && v.Version == at, cancellationToken)
                : null;
            var was = current is null ? null : StoredJson.Read<Still>(current.OutputJson);
            var next = 1 + (await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == shotKey).MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0);
            var aspect = project.Setup.Stills.Size.Aspect;
            var still = new Still(shotKey, file, was?.Prompt ?? "", 0, new GenerationSize(aspect, size.Width, size.Height), "", [], YourFile: true);
            db.CellVersions.Add(new CellVersionEntry
            {
                ProjectId = projectId,
                Stage = Stage,
                Key = shotKey,
                Version = next,
                Origin = VersionOrigin.Edited,
                BasedOn = cell.CurrentVersion,
                // What it stands in for: the still made from these inputs.
                InputHash = current?.InputHash ?? InputHash.Of(new { stage = Stage, shotKey, file }),
                SchemaVersion = Still.SchemaVersion,
                OutputJson = StoredJson.Write(still),
                CreatedAt = now,
            });
            cell.CurrentVersion = next;
            cell.ApprovedVersion = gated ? cell.ApprovedVersion : next;
            cell.State = state = gated ? StageState.NeedsReview : StageState.Approved;
            cell.Error = null;
            cell.ActivityJson = "[]";   // nothing ran: a log left from a failed make is not this picture's
            cell.Queued = false;        // your picture is what you wanted instead
            cell.QueuedPrompt = null;
            cell.UpdatedAt = now;
            if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } entry)
            {
                entry.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        runner.Publish(new StageUpdate(projectId, Stage, state, Key: shotKey));
        await runner.SettleAsync(projectId, Stage, cancellationToken);
    }

    private async Task ChangeAsync(Guid projectId, string shotKey, Action<CellEntry> change, CancellationToken cancellationToken)
    {
        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == shotKey, cancellationToken)
                ?? throw new KeyNotFoundException($"There is no still of {shotKey} yet.");
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
