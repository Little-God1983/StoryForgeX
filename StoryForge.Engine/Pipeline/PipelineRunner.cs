using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Pipeline;

/// <summary>
/// Runs stages, one job at a time for all projects (one GPU, one model loaded at a time). A queued
/// stage shows as running at once. After a stage, the run waits at its gate or goes on with the
/// next stage that exists; the stages arrive one issue at a time (Research in #5, Script in #6, …).
/// A stage with a result per segment (the script) keeps each segment in a cell of its own: it is
/// approved when every segment is, and a single segment can be written again on its own.
/// </summary>
internal sealed class PipelineRunner(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    IEnumerable<IStageWorker> workers,
    ProjectStore projects,
    TimeProvider clock) : IHostedService
{
    internal const string ClosedWhileRunning = "StoryForge was closed while this stage ran. Retry runs it again.";

    /// <summary>The key of a stage's own cell; a segment's cell has the segment's id.</summary>
    private const string Whole = "";

    private readonly Dictionary<PipelineStage, IStageWorker> _workers = workers.ToDictionary(w => w.Stage);
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid, PipelineStage, string), Job> _jobs = [];
    private readonly CancellationTokenSource _stopping = new();

    // One settle at a time: each reads every segment, and two at once could each miss the other's
    // change and leave a stage in review whose segments are all approved.
    private readonly SemaphoreSlim _settling = new(1, 1);
    private Task _loop = Task.CompletedTask;

    public event EventHandler<StageUpdate>? StageUpdated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _queue.Writer.TryComplete();
        await _loop.WaitAsync(cancellationToken);
    }

    /// <summary>Whether the stage (or with <paramref name="key"/>, that segment) is running or waiting to run.</summary>
    public bool IsBusy(Guid projectId, PipelineStage stage, string key = Whole)
    {
        lock (_lock)
        {
            return _jobs.ContainsKey((projectId, stage, key));
        }
    }

    /// <summary>Whether the stage or any of its segments is running or waiting to run.</summary>
    public bool IsAnyBusy(Guid projectId, PipelineStage stage)
    {
        lock (_lock)
        {
            return _jobs.Keys.Any(k => k.Item1 == projectId && k.Item2 == stage);
        }
    }

    /// <summary>
    /// The segments a running whole-stage run has stored so far; null when the stage is not running
    /// as a whole. The others are still to come in this run.
    /// </summary>
    public IReadOnlySet<string>? StoredInRun(Guid projectId, PipelineStage stage)
    {
        Job? job;
        lock (_lock)
        {
            _jobs.TryGetValue((projectId, stage, Whole), out job);
        }
        if (job is null)
        {
            return null;
        }
        lock (job.Stored)
        {
            return new HashSet<string>(job.Stored);
        }
    }

    /// <summary>What a running stage (or segment) has done so far; null when it is not running.</summary>
    public IReadOnlyList<ActivityLine>? LiveActivity(Guid projectId, PipelineStage stage, string key = Whole)
    {
        Job? job;
        lock (_lock)
        {
            _jobs.TryGetValue((projectId, stage, key), out job);
        }
        return job is null ? null : Snapshot(job.Activity);
    }

    /// <summary>Queues the first stage that has not been started; a stage at its gate or failed stays as it is.</summary>
    public async Task StartRunAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var next = project.Stages.FirstOrDefault(s => s.State != StageState.Approved);
        if (next is { State: StageState.NotStarted } && _workers.ContainsKey(next.Stage))
        {
            await EnqueueAsync(projectId, next.Stage, Whole, cancellationToken);
        }
    }

    public async Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        if (!_workers.ContainsKey(stage))
        {
            throw new InvalidOperationException($"The {stage} stage is not built yet.");
        }
        await EnqueueAsync(projectId, stage, Whole, cancellationToken);
    }

    /// <summary>Writes one segment of a stage again; the stage's other segments stay as they are.</summary>
    public async Task RegenerateSegmentAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        if (!_workers.TryGetValue(stage, out var worker) || worker is not ISegmentWorker)
        {
            throw new InvalidOperationException($"The {stage} stage has no segments to write one by one.");
        }
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Cells.AnyAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == key && c.CurrentVersion != null, cancellationToken))
            {
                throw new KeyNotFoundException($"The {stage} stage has no segment {key}.");
            }
        }
        await EnqueueAsync(projectId, stage, key, cancellationToken);
    }

    public async Task CancelAsync(Guid projectId, PipelineStage stage, string key = Whole)
    {
        Job? job;
        var waiting = false;
        lock (_lock)
        {
            if (_jobs.TryGetValue((projectId, stage, key), out job) && !job.Started)
            {
                // Still waiting its turn: it goes back now, not after the run before it, and the
                // queue skips it when it gets there. It stays listed until it is back, so a new run
                // of the stage cannot slip in between and be overwritten.
                job.Skipped = waiting = true;
            }
        }
        if (job is null)
        {
            return;
        }
        if (waiting)
        {
            // After EnqueueAsync has written Running and kept the state before, so that is what comes back.
            await job.Queued.Task;
            await RestoreAsync(job);
            return;
        }
        try
        {
            await job.Cancel.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // It finished in the moment between finding it and cancelling it.
        }
    }

    public async Task ApproveAsync(Guid projectId, PipelineStage stage, int version, CancellationToken cancellationToken)
    {
        if (IsBusy(projectId, stage))
        {
            throw new InvalidOperationException($"The {stage} stage is running. Approve it when it is done.");
        }
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == Whole, cancellationToken)
                ?? throw new KeyNotFoundException($"The {stage} stage of project {projectId} has no result yet.");
            if (!await db.CellVersions.AnyAsync(v => v.ProjectId == projectId && v.Stage == stage && v.Key == Whole && v.Version == version, cancellationToken))
            {
                throw new KeyNotFoundException($"The {stage} stage has no version {version}.");
            }
            cell.CurrentVersion = version;
            cell.ApprovedVersion = version;
            cell.State = StageState.Approved;
            cell.Error = null;
            await TouchAsync(db, cell, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        Publish(new StageUpdate(projectId, stage, StageState.Approved));
        await ContinueAsync(projectId, stage, cancellationToken);
    }

    /// <summary>
    /// After segments changed outside a run (approved, edited, switched to another version): the
    /// stage is approved when every segment is, else it waits for review; once approved, the run goes on.
    /// </summary>
    public async Task SettleAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        StageState? settled;
        await _settling.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            settled = await SettleAsync(db, projectId, stage, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _settling.Release();
        }
        if (settled is { } state)
        {
            Publish(new StageUpdate(projectId, stage, state));
            if (state == StageState.Approved)
            {
                await ContinueAsync(projectId, stage, cancellationToken);
            }
        }
    }

    /// <summary>Tells the app about a change made outside a run, e.g. an edited fact sheet.</summary>
    public void Publish(StageUpdate update)
    {
        try
        {
            StageUpdated?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            // A listener's failure is the listener's; the run goes on.
            Debug.WriteLine($"A StageUpdated listener failed: {ex}");
        }
    }

    /// <summary>The gate stops the run in "Stop at ticked gates" mode; the required gates stop it in every mode.</summary>
    internal static bool IsGated(ProjectSetup setup, PipelineStage stage) =>
        setup.Gates.Contains(stage) && (setup.Mode == RunMode.StopAtGates || ProjectSetup.RequiredGates.Contains(stage));

    /// <summary>
    /// Queues the next stage unless it has a result already: approving a stage again does not throw
    /// away what came after it. Regenerate writes that again (#11 marks it out of date).
    /// </summary>
    private async Task ContinueAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        var next = stage + 1;
        if (!Enum.IsDefined(next) || !_workers.ContainsKey(next))
        {
            return;
        }
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Cells.AnyAsync(c => c.ProjectId == projectId && c.Stage == next && c.Key == Whole && c.CurrentVersion != null, cancellationToken))
            {
                return;
            }
        }
        await EnqueueAsync(projectId, next, Whole, cancellationToken);
    }

    private async Task EnqueueAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken)
    {
        var job = new Job(projectId, stage, key, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
        lock (_lock)
        {
            // Checked with the adding, so neither can slip in beside the other: a whole run replaces
            // the segments, and a segment written alongside it would be lost or stale.
            if (key != Whole && _jobs.ContainsKey((projectId, stage, Whole)))
            {
                job.Cancel.Dispose();
                throw new InvalidOperationException($"The {stage} stage is being written. Try again when it is done.");
            }
            if (key == Whole && _jobs.Keys.Any(k => k.Item1 == projectId && k.Item2 == stage && k.Item3 != Whole))
            {
                job.Cancel.Dispose();
                throw new InvalidOperationException($"A segment of the {stage} stage is being written. Try again when it is done.");
            }
            if (!_jobs.TryAdd((projectId, stage, key), job))
            {
                job.Cancel.Dispose();
                return;   // already running or waiting to run
            }
        }
        try
        {
            await ChangeCellAsync(projectId, stage, key, cell =>
            {
                job.PreviousState = cell.State;
                job.PreviousError = cell.Error;
                job.PreviousActivity = cell.ActivityJson;
                cell.State = StageState.Running;
                cell.Error = null;
                cell.ActivityJson = "[]";
            }, cancellationToken);
        }
        catch
        {
            Forget(job);
            job.Queued.TrySetResult();
            throw;
        }
        Publish(Update(job, StageState.Running));
        _queue.Writer.TryWrite(job);
        job.Queued.TrySetResult();
    }

    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(job);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Recording the outcome failed (the database busy or the disk full). The cell may
                    // say Running until the next start marks it; the next job must still run.
                    Debug.WriteLine($"{job.Stage} {job.Key} of {job.ProjectId}: recording the outcome failed: {ex}");
                    Forget(job);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Closing: what is still queued is marked on the next start (RecoverAsync).
        }
    }

    private async Task RunAsync(Job job)
    {
        var activity = job.Activity;
        var reporter = new ActivityReporter(line =>
        {
            lock (activity)
            {
                activity.Add(line);
            }
            Publish(Update(job, StageState.Running, line));
        });
        lock (_lock)
        {
            if (job.Skipped)
            {
                job.Cancel.Dispose();
                return;   // cancelled while it waited; CancelAsync already put it back
            }
            job.Started = true;
        }
        var goOn = false;
        try
        {
            job.Cancel.Token.ThrowIfCancellationRequested();
            var project = await projects.GetAsync(job.ProjectId, job.Cancel.Token);
            var gated = IsGated(project.Setup, job.Stage);
            var context = new StageContext(project, reporter, job.Key == Whole ? segment => StoreRunSegmentAsync(job, segment, gated) : null);
            if (job.Key == Whole)
            {
                var result = await _workers[job.Stage].RunAsync(context, job.Cancel.Token);
                var state = await StoreAsync(job, result, gated, Snapshot(activity));
                Forget(job);
                Publish(Update(job, state));
                goOn = !gated;
            }
            else
            {
                var result = await ((ISegmentWorker)_workers[job.Stage]).RunSegmentAsync(context, job.Key, job.Cancel.Token);
                var (state, stageState) = await StoreSegmentAsync(job, result, gated, Snapshot(activity));
                Forget(job);
                Publish(Update(job, state));
                if (stageState is { } settled)
                {
                    Publish(new StageUpdate(job.ProjectId, job.Stage, settled));
                    goOn = settled == StageState.Approved;
                }
            }
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            if (_stopping.IsCancellationRequested)
            {
                await FailAsync(job, ClosedWhileRunning, Snapshot(activity));
            }
            else if (StoredCount(job) is > 0 and var stored)
            {
                // Segments of this run are stored already: going back to before would hide them.
                await FailAsync(job, $"Cancelled after {stored} {(stored == 1 ? "segment" : "segments")}. Those are kept; Retry makes them all again.", Snapshot(activity));
            }
            else
            {
                await RestoreAsync(job);
            }
        }
        catch (StageFailedException ex)
        {
            await FailAsync(job, ex.Message, Snapshot(activity));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{job.Stage} {job.Key} of {job.ProjectId} failed: {ex}");
            await FailAsync(job, $"Something went wrong: {ex.Message}", Snapshot(activity));
        }
        finally
        {
            Forget(job);
            job.Cancel.Dispose();
        }

        // Outside the stage's own try: the stage is stored and approved, and a next stage that
        // cannot be queued must not mark it failed.
        if (goOn)
        {
            try
            {
                await ContinueAsync(job.ProjectId, job.Stage, _stopping.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Queueing the stage after {job.Stage} of {job.ProjectId} failed: {ex}");
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
        }
    }

    private async Task<StageState> StoreAsync(Job job, StageResult result, bool gated, List<ActivityLine> activity)
    {
        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == job.ProjectId && c.Stage == job.Stage && c.Key == Whole);
        AddVersion(db, cell, await NextVersionAsync(db, job.ProjectId, job.Stage, Whole), result.OutputJson, result.SchemaVersion, result.InputHash);
        cell.State = gated ? StageState.NeedsReview : StageState.Approved;
        cell.ApprovedVersion = gated ? cell.ApprovedVersion : cell.CurrentVersion;
        cell.Error = null;
        cell.ActivityJson = StoredJson.Write(activity);

        if (result.Segments is { } segments)
        {
            // A new whole result replaces the segments: each gets the new text as its next version
            // (to review again when the stage is gated); segments the new result lacks are gone.
            var existing = await db.Cells.Where(c => c.ProjectId == job.ProjectId && c.Stage == job.Stage && c.Key != Whole).ToListAsync();
            db.Cells.RemoveRange(existing.Where(c => segments.All(s => s.Key != c.Key)));
            foreach (var segment in segments)
            {
                var child = existing.FirstOrDefault(c => c.Key == segment.Key);
                if (child is null)
                {
                    child = new CellEntry { ProjectId = job.ProjectId, Stage = job.Stage, Key = segment.Key };
                    db.Cells.Add(child);
                }
                if (StoredCount(job, segment.Key) > 0)
                {
                    continue;   // stored while the run went on, and maybe approved or switched since
                }
                AddVersion(db, child, await NextVersionAsync(db, job.ProjectId, job.Stage, segment.Key), segment.OutputJson, segment.SchemaVersion, segment.InputHash);
                child.State = gated ? StageState.NeedsReview : StageState.Approved;
                child.ApprovedVersion = gated ? null : child.CurrentVersion;
                child.Error = null;
                child.UpdatedAt = clock.GetUtcNow();
            }
        }
        await TouchAsync(db, cell, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        return cell.State;
    }

    /// <summary>
    /// Stores one segment while its whole-stage run goes on (see <see cref="StageContext.StoreSegment"/>):
    /// as the segment's next version, to review or approved as the gate says. The stage stays running.
    /// </summary>
    private async Task StoreRunSegmentAsync(Job job, CellResult result, bool gated)
    {
        StageState state;
        await using (var db = await contextFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == job.ProjectId && c.Stage == job.Stage && c.Key == result.Key);
            if (cell is null)
            {
                cell = new CellEntry { ProjectId = job.ProjectId, Stage = job.Stage, Key = result.Key };
                db.Cells.Add(cell);
            }
            AddVersion(db, cell, await NextVersionAsync(db, job.ProjectId, job.Stage, result.Key), result.OutputJson, result.SchemaVersion, result.InputHash);
            cell.State = state = gated ? StageState.NeedsReview : StageState.Approved;
            cell.ApprovedVersion = gated ? null : cell.CurrentVersion;
            cell.Error = null;
            cell.ActivityJson = "[]";
            await TouchAsync(db, cell, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        lock (job.Stored)
        {
            job.Stored.Add(result.Key);
        }
        Publish(new StageUpdate(job.ProjectId, job.Stage, state, Key: result.Key));
    }

    /// <summary>How many segments the run stored as it went; with <paramref name="key"/>, whether it stored that one.</summary>
    private static int StoredCount(Job job, string? key = null)
    {
        lock (job.Stored)
        {
            return key is null ? job.Stored.Count : job.Stored.Contains(key) ? 1 : 0;
        }
    }

    /// <summary>Stores a rewritten segment and settles the stage; the stage's new state, if it changed.</summary>
    private async Task<(StageState Segment, StageState? Stage)> StoreSegmentAsync(Job job, CellResult result, bool gated, List<ActivityLine> activity)
    {
        await _settling.WaitAsync(CancellationToken.None);
        try
        {
            return await StoreSegmentSettledAsync(job, result, gated, activity);
        }
        finally
        {
            _settling.Release();
        }
    }

    private async Task<(StageState Segment, StageState? Stage)> StoreSegmentSettledAsync(Job job, CellResult result, bool gated, List<ActivityLine> activity)
    {
        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == job.ProjectId && c.Stage == job.Stage && c.Key == job.Key);
        AddVersion(db, cell, await NextVersionAsync(db, job.ProjectId, job.Stage, job.Key), result.OutputJson, result.SchemaVersion, result.InputHash);
        cell.State = gated ? StageState.NeedsReview : StageState.Approved;
        cell.ApprovedVersion = gated ? cell.ApprovedVersion : cell.CurrentVersion;
        cell.Error = null;
        cell.ActivityJson = StoredJson.Write(activity);
        await TouchAsync(db, cell, CancellationToken.None);
        var stage = await SettleAsync(db, job.ProjectId, job.Stage, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        return (cell.State, stage);
    }

    /// <summary>
    /// The stage's state from its segments: approved when every segment is, else waiting for review.
    /// A running stage is left as it is. A failed one (its last run failed) keeps its failure until
    /// every segment of the script before it is approved. Returns the new state when it changed.
    /// </summary>
    private async Task<StageState?> SettleAsync(StoryForgeDbContext db, Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == Whole, cancellationToken);
        if (cell is null || cell.State is not (StageState.NeedsReview or StageState.Approved or StageState.Failed))
        {
            return null;
        }
        // Tracked cells first: changes made in this context are not in the database yet.
        var segments = await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key != Whole).ToListAsync(cancellationToken);
        var state = segments.Count > 0 && segments.All(s => s.State == StageState.Approved) ? StageState.Approved : StageState.NeedsReview;
        if (state == cell.State || (cell.State == StageState.Failed && (state != StageState.Approved || cell.CurrentVersion is null)))
        {
            return null;
        }
        cell.State = state;
        cell.Error = null;
        cell.ApprovedVersion = state == StageState.Approved ? cell.CurrentVersion : cell.ApprovedVersion;
        cell.UpdatedAt = clock.GetUtcNow();
        return state;
    }

    private void AddVersion(StoryForgeDbContext db, CellEntry cell, int version, string outputJson, int schemaVersion, string inputHash)
    {
        db.CellVersions.Add(new CellVersionEntry
        {
            ProjectId = cell.ProjectId,
            Stage = cell.Stage,
            Key = cell.Key,
            Version = version,
            Origin = VersionOrigin.Generated,
            InputHash = inputHash,
            SchemaVersion = schemaVersion,
            OutputJson = outputJson,
            CreatedAt = clock.GetUtcNow(),
        });
        cell.CurrentVersion = version;
    }

    private static async Task<int> NextVersionAsync(StoryForgeDbContext db, Guid projectId, PipelineStage stage, string key) =>
        1 + (await db.CellVersions
            .Where(v => v.ProjectId == projectId && v.Stage == stage && v.Key == key)
            .MaxAsync(v => (int?)v.Version) ?? 0);

    private async Task FailAsync(Job job, string reason, List<ActivityLine> activity)
    {
        await ChangeCellAsync(job.ProjectId, job.Stage, job.Key, cell =>
        {
            cell.State = StageState.Failed;
            cell.Error = reason;
            cell.ActivityJson = StoredJson.Write(activity);
        }, CancellationToken.None);
        Forget(job);
        Publish(Update(job, StageState.Failed));
        if (job.Key != Whole)
        {
            await SettleAsync(job.ProjectId, job.Stage, CancellationToken.None);   // an approved stage has a failed segment now
        }
    }

    /// <summary>
    /// A cancelled stage goes back to where it stood, with the log that belongs to that: a failure
    /// keeps the log that explains it. The last result, if any, is still there.
    /// </summary>
    private async Task RestoreAsync(Job job)
    {
        await ChangeCellAsync(job.ProjectId, job.Stage, job.Key, cell =>
        {
            cell.State = job.PreviousState;
            cell.Error = job.PreviousError;
            cell.ActivityJson = job.PreviousActivity;
        }, CancellationToken.None);
        Forget(job);
        Publish(Update(job, job.PreviousState));
    }

    /// <summary>
    /// Changes the cell, and the project's "last changed" with it. A stage's cell is made on first use;
    /// a segment's is not: one a new script took away stays away.
    /// </summary>
    private async Task ChangeCellAsync(Guid projectId, PipelineStage stage, string key, Action<CellEntry> change, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == key, cancellationToken);
        if (cell is null && key != Whole)
        {
            return;
        }
        if (cell is null)
        {
            cell = new CellEntry { ProjectId = projectId, Stage = stage, Key = key, State = StageState.NotStarted };
            db.Cells.Add(cell);
        }
        change(cell);
        await TouchAsync(db, cell, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task TouchAsync(StoryForgeDbContext db, CellEntry cell, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        cell.UpdatedAt = now;
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == cell.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Stages and segments left running when the app last closed (or crashed) are marked failed, so
    /// Retry is offered; a stage with a failed segment is not approved.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await db.Cells.Where(c => c.State == StageState.Running).ToListAsync(cancellationToken);
        foreach (var cell in interrupted)
        {
            cell.State = StageState.Failed;
            cell.Error = ClosedWhileRunning;
        }
        foreach (var (projectId, stage) in interrupted.Where(c => c.Key != Whole).Select(c => (c.ProjectId, c.Stage)).Distinct())
        {
            await SettleAsync(db, projectId, stage, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private void Forget(Job job)
    {
        lock (_lock)
        {
            // Only this job: a new run of the same stage may already be queued under the key.
            if (_jobs.TryGetValue((job.ProjectId, job.Stage, job.Key), out var current) && current == job)
            {
                _jobs.Remove((job.ProjectId, job.Stage, job.Key));
            }
        }
    }

    private static StageUpdate Update(Job job, StageState state, ActivityLine? line = null) =>
        new(job.ProjectId, job.Stage, state, line, job.Key == Whole ? null : job.Key);

    private static List<ActivityLine> Snapshot(List<ActivityLine> activity)
    {
        lock (activity)
        {
            return [.. activity];
        }
    }

    private sealed class Job(Guid projectId, PipelineStage stage, string key, CancellationTokenSource cancel)
    {
        public Guid ProjectId { get; } = projectId;

        public PipelineStage Stage { get; } = stage;

        /// <summary>"" for the stage as a whole, else the segment.</summary>
        public string Key { get; } = key;

        public CancellationTokenSource Cancel { get; } = cancel;

        public StageState PreviousState { get; set; }

        public string? PreviousError { get; set; }

        public string PreviousActivity { get; set; } = "[]";

        /// <summary>Done once EnqueueAsync has marked the cell Running and queued the job.</summary>
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The loop has picked it up; set under the runner's lock.</summary>
        public bool Started { get; set; }

        /// <summary>Cancelled while it waited; the loop drops it. Set under the runner's lock.</summary>
        public bool Skipped { get; set; }

        /// <summary>What the stage has done so far; locked while written or copied.</summary>
        public List<ActivityLine> Activity { get; } = [];

        /// <summary>The segments stored while the run went on; locked while written or read.</summary>
        public HashSet<string> Stored { get; } = [];
    }

    /// <summary>Reports on the stage's own thread, at once; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class ActivityReporter(Action<ActivityLine> report) : IProgress<ActivityLine>
    {
        public void Report(ActivityLine value) => report(value);
    }
}
