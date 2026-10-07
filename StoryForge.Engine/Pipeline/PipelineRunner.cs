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
/// </summary>
internal sealed class PipelineRunner(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    IEnumerable<IStageWorker> workers,
    ProjectStore projects,
    TimeProvider clock) : IHostedService
{
    internal const string ClosedWhileRunning = "StoryForge was closed while this stage ran. Retry runs it again.";

    private readonly Dictionary<PipelineStage, IStageWorker> _workers = workers.ToDictionary(w => w.Stage);
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid, PipelineStage), Job> _jobs = [];
    private readonly CancellationTokenSource _stopping = new();
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

    /// <summary>Whether the stage is running or waiting to run for this project.</summary>
    public bool IsBusy(Guid projectId, PipelineStage stage)
    {
        lock (_lock)
        {
            return _jobs.ContainsKey((projectId, stage));
        }
    }

    /// <summary>What a running stage has done so far; null when it is not running.</summary>
    public IReadOnlyList<ActivityLine>? LiveActivity(Guid projectId, PipelineStage stage)
    {
        Job? job;
        lock (_lock)
        {
            _jobs.TryGetValue((projectId, stage), out job);
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
            await EnqueueAsync(projectId, next.Stage, cancellationToken);
        }
    }

    public async Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        if (!_workers.ContainsKey(stage))
        {
            throw new InvalidOperationException($"The {stage} stage is not built yet.");
        }
        await EnqueueAsync(projectId, stage, cancellationToken);
    }

    public async Task CancelAsync(Guid projectId, PipelineStage stage)
    {
        Job? job;
        var waiting = false;
        lock (_lock)
        {
            if (_jobs.TryGetValue((projectId, stage), out job) && !job.Started)
            {
                // Still waiting its turn: it goes back now, not after the run before it, and the
                // queue skips it when it gets there.
                job.Skipped = waiting = true;
                _jobs.Remove((projectId, stage));
            }
        }
        if (job is null)
        {
            return;
        }
        if (waiting)
        {
            await RestoreAsync(job, Snapshot(job.Activity));
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
            var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == "", cancellationToken)
                ?? throw new KeyNotFoundException($"The {stage} stage of project {projectId} has no result yet.");
            if (!await db.CellVersions.AnyAsync(v => v.ProjectId == projectId && v.Stage == stage && v.Key == "" && v.Version == version, cancellationToken))
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

    private async Task ContinueAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        var next = stage + 1;
        if (Enum.IsDefined(next) && _workers.ContainsKey(next))
        {
            await EnqueueAsync(projectId, next, cancellationToken);
        }
    }

    private async Task EnqueueAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        var job = new Job(projectId, stage, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
        lock (_lock)
        {
            if (!_jobs.TryAdd((projectId, stage), job))
            {
                return;   // already running or waiting to run
            }
        }
        try
        {
            await ChangeCellAsync(projectId, stage, cell =>
            {
                job.PreviousState = cell.State;
                job.PreviousError = cell.Error;
                cell.State = StageState.Running;
                cell.Error = null;
                cell.ActivityJson = "[]";
            }, cancellationToken);
        }
        catch
        {
            Forget(job);
            throw;
        }
        Publish(new StageUpdate(projectId, stage, StageState.Running));
        _queue.Writer.TryWrite(job);
    }

    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(job);
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
            Publish(new StageUpdate(job.ProjectId, job.Stage, StageState.Running, line));
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
            var result = await _workers[job.Stage].RunAsync(new StageContext(project, reporter), job.Cancel.Token);
            var gated = IsGated(project.Setup, job.Stage);
            var state = await StoreAsync(job, result, gated, Snapshot(activity));
            Forget(job);
            Publish(new StageUpdate(job.ProjectId, job.Stage, state));
            goOn = !gated;
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            if (_stopping.IsCancellationRequested)
            {
                await FailAsync(job, ClosedWhileRunning, Snapshot(activity));
            }
            else
            {
                await RestoreAsync(job, Snapshot(activity));
            }
        }
        catch (StageFailedException ex)
        {
            await FailAsync(job, ex.Message, Snapshot(activity));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{job.Stage} of {job.ProjectId} failed: {ex}");
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
        var cell = await db.Cells.FirstAsync(c => c.ProjectId == job.ProjectId && c.Stage == job.Stage && c.Key == "");
        var version = 1 + (await db.CellVersions
            .Where(v => v.ProjectId == job.ProjectId && v.Stage == job.Stage && v.Key == "")
            .MaxAsync(v => (int?)v.Version) ?? 0);
        db.CellVersions.Add(new CellVersionEntry
        {
            ProjectId = job.ProjectId,
            Stage = job.Stage,
            Version = version,
            Origin = VersionOrigin.Generated,
            InputHash = result.InputHash,
            SchemaVersion = result.SchemaVersion,
            OutputJson = result.OutputJson,
            CreatedAt = clock.GetUtcNow(),
        });
        cell.CurrentVersion = version;
        cell.State = gated ? StageState.NeedsReview : StageState.Approved;
        cell.ApprovedVersion = gated ? cell.ApprovedVersion : version;
        cell.Error = null;
        cell.ActivityJson = StoredJson.Write(activity);
        await TouchAsync(db, cell, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        return cell.State;
    }

    private async Task FailAsync(Job job, string reason, List<ActivityLine> activity)
    {
        await ChangeCellAsync(job.ProjectId, job.Stage, cell =>
        {
            cell.State = StageState.Failed;
            cell.Error = reason;
            cell.ActivityJson = StoredJson.Write(activity);
        }, CancellationToken.None);
        Forget(job);
        Publish(new StageUpdate(job.ProjectId, job.Stage, StageState.Failed));
    }

    /// <summary>A cancelled stage goes back to where it stood; the last result, if any, is still there.</summary>
    private async Task RestoreAsync(Job job, List<ActivityLine> activity)
    {
        activity.Add(new ActivityLine(clock.GetUtcNow(), Client.ActivityKind.Check, "cancelled"));
        await ChangeCellAsync(job.ProjectId, job.Stage, cell =>
        {
            cell.State = job.PreviousState;
            cell.Error = job.PreviousError;
            cell.ActivityJson = StoredJson.Write(activity);
        }, CancellationToken.None);
        Forget(job);
        Publish(new StageUpdate(job.ProjectId, job.Stage, job.PreviousState));
    }

    /// <summary>Changes the cell (made on first use), and the project's "last changed" with it.</summary>
    private async Task ChangeCellAsync(Guid projectId, PipelineStage stage, Action<CellEntry> change, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == "", cancellationToken);
        if (cell is null)
        {
            cell = new CellEntry { ProjectId = projectId, Stage = stage, State = StageState.NotStarted };
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

    /// <summary>Stages left running when the app last closed (or crashed) are marked failed, so Retry is offered.</summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await db.Cells.Where(c => c.State == StageState.Running).ToListAsync(cancellationToken);
        foreach (var cell in interrupted)
        {
            cell.State = StageState.Failed;
            cell.Error = ClosedWhileRunning;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private void Forget(Job job)
    {
        lock (_lock)
        {
            // Only this job: a new run of the same stage may already be queued under the key.
            if (_jobs.TryGetValue((job.ProjectId, job.Stage), out var current) && current == job)
            {
                _jobs.Remove((job.ProjectId, job.Stage));
            }
        }
    }

    private static List<ActivityLine> Snapshot(List<ActivityLine> activity)
    {
        lock (activity)
        {
            return [.. activity];
        }
    }

    private sealed class Job(Guid projectId, PipelineStage stage, CancellationTokenSource cancel)
    {
        public Guid ProjectId { get; } = projectId;

        public PipelineStage Stage { get; } = stage;

        public CancellationTokenSource Cancel { get; } = cancel;

        public StageState PreviousState { get; set; }

        public string? PreviousError { get; set; }

        /// <summary>The loop has picked it up; set under the runner's lock.</summary>
        public bool Started { get; set; }

        /// <summary>Cancelled while it waited; the loop drops it. Set under the runner's lock.</summary>
        public bool Skipped { get; set; }

        /// <summary>What the stage has done so far; locked while written or copied.</summary>
        public List<ActivityLine> Activity { get; } = [];
    }

    /// <summary>Reports on the stage's own thread, at once; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class ActivityReporter(Action<ActivityLine> report) : IProgress<ActivityLine>
    {
        public void Report(ActivityLine value) => report(value);
    }
}
