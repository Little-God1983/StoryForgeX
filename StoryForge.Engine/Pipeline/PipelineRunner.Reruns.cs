using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine.Pipeline;

/// <summary>
/// How a rerun (Rerun pipeline from here) goes on. It marks the cells it makes again (or keeps);
/// once every marked cell of a stage is approved (at once without the gate, else when you approve
/// it), what is made from them is made next, and marked in turn: a segment's script → its voice →
/// its shots, cut again with the other segments' shots left as they are → their stills. From a
/// reference image, the stills that use it. Nothing else is touched. Cancel on a marked cell stops
/// the rerun there.
/// </summary>
internal sealed partial class PipelineRunner
{
    /// <summary>The stages a rerun marks cells of, in order; the stills, last for now, are not marked.</summary>
    private static readonly PipelineStage[] RerunStages = [PipelineStage.Script, PipelineStage.Voice, PipelineStage.Storyboard, PipelineStage.References];

    // One step at a time: two at once could both see the same marked cells approved and make what follows twice.
    private readonly SemaphoreSlim _advancing = new(1, 1);

    /// <summary>The segment a shot's key belongs to: "S04" of "S04/3f9a".</summary>
    internal static string ShotSegment(string shotKey) => shotKey.Split('/')[0];

    /// <summary>Goes on with every rerun of the project whose marked cells are approved now; one still waiting stays as it is.</summary>
    public async Task AdvanceRerunsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await _advancing.WaitAsync(cancellationToken);
        try
        {
            foreach (var stage in RerunStages)
            {
                await AdvanceAsync(projectId, stage, cancellationToken);
            }
        }
        finally
        {
            _advancing.Release();
        }
    }

    /// <summary>Marks (or unmarks) cells as part of a rerun.</summary>
    internal async Task MarkAsync(Guid projectId, PipelineStage stage, IReadOnlyCollection<string> keys, bool rerun, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == stage && keys.Contains(c.Key))
            .ExecuteUpdateAsync(c => c.SetProperty(e => e.Rerun, rerun), cancellationToken);
    }

    private async Task AdvanceAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        List<CellEntry> marked;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            marked = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Stage == stage && c.Rerun).ToListAsync(cancellationToken);
        }
        // Each waits for its approval: at its gate, or after a failure for Retry.
        if (marked.Count == 0 || marked.Any(c => c.State != StageState.Approved) || IsAnyBusy(projectId, stage))
        {
            return;
        }
        var keys = marked.Select(c => c.Key).ToHashSet();
        switch (stage)
        {
            case PipelineStage.Script:
                await SpeakAgainAsync(projectId, keys, cancellationToken);
                break;
            case PipelineStage.Voice:
                await CutAgainAsync(projectId, keys, cancellationToken);
                break;
            default:
                await PaintAgainAsync(projectId, stage, keys, cancellationToken);
                break;
        }
    }

    /// <summary>The segments' voices, spoken again from their script; a segment not spoken yet ends there.</summary>
    private async Task SpeakAgainAsync(Guid projectId, HashSet<string> keys, CancellationToken cancellationToken)
    {
        List<string> spoken = [];
        if (_workers.ContainsKey(PipelineStage.Voice))
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            spoken = await db.Cells.AsNoTracking()
                .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Voice && keys.Contains(c.Key) && c.CurrentVersion != null)
                .Select(c => c.Key).ToListAsync(cancellationToken);
        }
        foreach (var key in spoken)
        {
            try
            {
                await EnqueueAsync(projectId, PipelineStage.Voice, key, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return;   // the whole voice is being spoken: after it, this goes on
            }
            await MarkAsync(projectId, PipelineStage.Voice, [key], true, cancellationToken);
            await MarkAsync(projectId, PipelineStage.Script, [key], false, cancellationToken);
        }
        await MarkAsync(projectId, PipelineStage.Script, [.. keys.Except(spoken)], false, cancellationToken);
    }

    /// <summary>
    /// The segments' shots, cut again from their new voices. The voices stay marked until the new
    /// shots are stored: when the cut fails, Retry on the storyboard cuts these segments again.
    /// </summary>
    private async Task CutAgainAsync(Guid projectId, HashSet<string> keys, CancellationToken cancellationToken)
    {
        CellEntry? board;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            board = await db.Cells.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == PipelineStage.Storyboard && c.Key == Whole, cancellationToken);
        }
        if (!_workers.ContainsKey(PipelineStage.Storyboard) || board?.CurrentVersion is null)
        {
            // Never planned: the pipeline plans the whole storyboard when it gets there.
            await MarkAsync(projectId, PipelineStage.Voice, keys, false, cancellationToken);
            return;
        }
        // A storyboard run, or stills made from the shots that would go: after it. A failed cut waits for Retry.
        if (board.State == StageState.Failed || IsAnyBusy(projectId, PipelineStage.Storyboard) || IsAnyBusy(projectId, PipelineStage.Stills))
        {
            return;
        }
        try
        {
            await EnqueueAsync(projectId, PipelineStage.Storyboard, Whole, cancellationToken, scope: keys);
        }
        catch (InvalidOperationException)
        {
            // A shot is being planned again: after it, this goes on.
        }
    }

    /// <summary>
    /// The stills of the marked shots, or of the stills that use the marked reference images, made
    /// again in one run with the still after each that starts from it (one step at most). Your own
    /// pictures stay. A still missing (a shot cut anew) is made in the same run.
    /// </summary>
    private async Task PaintAgainAsync(Guid projectId, PipelineStage stage, HashSet<string> keys, CancellationToken cancellationToken)
    {
        if (!_workers.ContainsKey(PipelineStage.Stills) || await cells.StateAsync(projectId, PipelineStage.Stills, cancellationToken) == StageState.NotStarted)
        {
            // Never made: the pipeline makes them all when it gets there.
            await MarkAsync(projectId, stage, keys, false, cancellationToken);
            return;
        }
        if (IsAnyBusy(projectId, PipelineStage.Stills) || await cells.StateAsync(projectId, PipelineStage.References, cancellationToken) != StageState.Approved)
        {
            return;   // the stills are made from the approved references: once they are, this goes on
        }
        var shots = (await cells.CurrentShotsAsync(projectId, cancellationToken)).Select(s => s.Key).ToHashSet();
        var stills = await cells.CurrentStillsAsync(projectId, cancellationToken);
        var redo = Redo(stage, keys, stills, shots);
        if (redo.Count == 0 && shots.All(stills.ContainsKey))
        {
            await MarkAsync(projectId, stage, keys, false, cancellationToken);
            return;
        }
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await db.Cells.Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Stills && redo.Contains(c.Key))
                .ExecuteUpdateAsync(c => c.SetProperty(e => e.Queued, true), cancellationToken);
        }
        try
        {
            await EnqueueAsync(projectId, PipelineStage.Stills, Whole, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;   // a still is being made on its own: after it, this goes on
        }
        await MarkAsync(projectId, stage, keys, false, cancellationToken);
    }

    /// <summary>
    /// The stills a rerun makes again from these shots (or these reference images), and the ones that
    /// start from those or from a shot that is gone; your own pictures stay.
    /// </summary>
    internal static HashSet<string> Redo(PipelineStage stage, IReadOnlySet<string> keys, IReadOnlyDictionary<string, Still> stills, IReadOnlySet<string> shots)
    {
        var direct = stills.Where(s => !s.Value.YourFile && (stage == PipelineStage.Storyboard ? keys.Contains(s.Key) : s.Value.References.Any(keys.Contains)))
            .Select(s => s.Key).ToHashSet();
        var redo = new HashSet<string>(direct);
        foreach (var (key, still) in stills)
        {
            if (!still.YourFile && still.ContinuesFrom is { } from && (direct.Contains(from) || !shots.Contains(from)))
            {
                redo.Add(key);
            }
        }
        return redo;
    }

    /// <summary>After a rerun's cut failed: the segments Retry on the storyboard cuts again; null for the whole storyboard.</summary>
    private async Task<IReadOnlySet<string>?> FailedRecutAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        if (stage != PipelineStage.Storyboard)
        {
            return null;
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Cells.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == Whole, cancellationToken) is not { State: StageState.Failed })
        {
            return null;
        }
        var voices = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == PipelineStage.Voice && c.Rerun && c.State == StageState.Approved)
            .Select(c => c.Key).ToListAsync(cancellationToken);
        return voices.Count > 0 ? voices.ToHashSet() : null;
    }

    /// <summary>A cancelled run stops the rerun it was part of: the cell it made, or the segments it cut again.</summary>
    private async Task StopRerunAsync(Job job)
    {
        if (job.Key != Whole)
        {
            await MarkAsync(job.ProjectId, job.Stage, [job.Key], false, CancellationToken.None);
        }
        else if (job.Scope is { } scope)
        {
            await MarkAsync(job.ProjectId, PipelineStage.Voice, scope, false, CancellationToken.None);
        }
    }
}
