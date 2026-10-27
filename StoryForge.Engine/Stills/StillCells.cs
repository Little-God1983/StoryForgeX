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
/// Continue pipeline makes the batch, Replace with file… puts your own picture in.
/// </summary>
internal sealed class StillCells(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    CellReader cells,
    ProjectStore projects,
    ProjectFolders folders,
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
        // While every still is made, one this run has not reached yet is still to come.
        var stored = runner.StoredInRun(projectId, Stage);
        var stills = stageCells.Where(c => c.Key != "").Select(cell =>
        {
            var own = versions.Where(v => v.Key == cell.Key).OrderBy(v => v.Version).ToList();
            var current = own.FirstOrDefault(v => v.Version == cell.CurrentVersion);
            var still = current is null ? null : StoredJson.Read<Still>(current.OutputJson);
            var path = still is null ? null : Path.Combine(root, still.File);
            return new StillView(
                cell.Key,
                stored is not null && !stored.Contains(cell.Key) ? StageState.Running : cell.State,
                cell.CurrentVersion ?? 0,
                cell.ApprovedVersion,
                [.. own.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
                still,
                path is not null && File.Exists(path) ? path : null,
                current?.InputHash ?? "",
                cell.Queued,
                cell.QueuedPrompt,
                cell.State == StageState.Failed ? cell.Error : null,
                runner.LiveActivity(projectId, Stage, cell.Key) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson));
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
    /// <param name="prompt">Your prompt, used as it is; null or blank for one Claude writes.</param>
    public async Task QueueAsync(Guid projectId, string shotKey, string? prompt, CancellationToken cancellationToken)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        segments.RequireIdle(projectId, Stage, shotKey);
        await ChangeAsync(projectId, shotKey, cell =>
        {
            cell.Queued = true;
            cell.QueuedPrompt = prompt;
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
    /// Continue pipeline: every still in the batch is made again, in the order of the shots, one
    /// after another (the model loads once). Stills never made (references approved before this
    /// stage existed) are all made.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is waiting, or the stills are being made.</exception>
    public async Task ContinueAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (runner.IsBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The stills are being made. Try again when they are done.");
        }
        await projects.GetAsync(projectId, cancellationToken);
        List<CellEntry> queued;
        bool started;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            started = await db.Cells.AnyAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "" && c.State != StageState.NotStarted, cancellationToken);
            queued = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == Stage && c.Key != "" && c.Queued).ToListAsync(cancellationToken);
        }
        if (!started)
        {
            if (await cells.StateAsync(projectId, PipelineStage.References, cancellationToken) != StageState.Approved)
            {
                throw new InvalidOperationException("Approve the references first: the stills are made from them.");
            }
            await runner.RegenerateAsync(projectId, Stage, cancellationToken);
            return;
        }
        if (queued.Count == 0)
        {
            throw new InvalidOperationException("No still is waiting to be made again: click Regenerate on the ones you want first.");
        }
        // In the order of the shots: a continuation is made after the still it starts from.
        var order = (await cells.CurrentShotsAsync(projectId, cancellationToken)).Select(s => s.Key).ToList();
        var batch = queued
            .OrderBy(c => order.IndexOf(c.Key) is >= 0 and var at ? at : int.MaxValue)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => (c.Key, c.QueuedPrompt))
            .ToList();
        await runner.EnqueueBatchAsync(projectId, Stage, batch, cancellationToken);
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
            await db.SaveChangesAsync(cancellationToken);
            state = cell.State;
        }
        runner.Publish(new StageUpdate(projectId, Stage, state, Key: shotKey));
    }
}
