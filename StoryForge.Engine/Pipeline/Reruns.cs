using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Pipeline;

/// <summary>
/// Rerun pipeline from here: what it would make again, as the question before it says, and the
/// start. The cell is made again, unless it is your own work (your edit of a segment or a shot,
/// your own picture): that stays, and what is made from it is made again. From a reference image,
/// only the stills that use it. How the rerun then goes on is <see cref="PipelineRunner"/>'s.
/// </summary>
internal sealed class Reruns(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    StillCells stillCells,
    CellReader cells,
    ProjectStore projects)
{
    /// <summary>What a rerun from a cell does.</summary>
    /// <param name="Keep">The cell is your own work and stays; what is made from it is made again.</param>
    /// <param name="Stills">The stills it makes again (from a still: in the batch).</param>
    /// <param name="Nothing">Nothing would be made: the text says why, and the rerun is refused.</param>
    private sealed record Plan(string Text, bool Keep, IReadOnlyList<string> Stills, bool Nothing);

    /// <summary>"This remakes S04's voice, its 4 shots and their 4 stills. Other segments stay as they are."</summary>
    public async Task<RerunPlan> PlanAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken)
    {
        var plan = await PlanOfAsync(projectId, stage, key, cancellationToken);
        return new RerunPlan(plan.Text, !plan.Nothing);
    }

    /// <exception cref="InvalidOperationException">The cell is being made, or nothing would be made from it.</exception>
    public async Task StartAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken)
    {
        var plan = await PlanOfAsync(projectId, stage, key, cancellationToken);
        if (plan.Nothing)
        {
            throw new InvalidOperationException(plan.Text);
        }
        if (stage == PipelineStage.Stills)
        {
            var waiting = await cells.QueuedStillsAsync(projectId, cancellationToken);
            var queued = new List<string>();
            try
            {
                foreach (var still in plan.Stills)
                {
                    await stillCells.QueueAsync(projectId, still, null, cancellationToken);
                    queued.Add(still);
                }
                await stillCells.ContinueAsync(projectId, cancellationToken);
            }
            catch
            {
                // Not made after all (the references in review, a still being made): out of the batch again, as they were.
                foreach (var still in queued.Where(k => !waiting.ContainsKey(k)))
                {
                    await stillCells.UnqueueAsync(projectId, still, CancellationToken.None);
                }
                throw;
            }
            return;
        }
        segments.RequireIdle(projectId, stage, key);
        await runner.StartRerunAsync(projectId, stage, key, remake: !plan.Keep, cancellationToken);   // a reference image's plan always keeps it
    }

    /// <summary>Stop the rerun: what waits for approval stays as it is, and nothing more is made after it.</summary>
    public async Task StopAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        List<PipelineStage> stages;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            stages = await db.Cells.Where(c => c.ProjectId == projectId && c.Rerun).Select(c => c.Stage).Distinct().ToListAsync(cancellationToken);
            await db.Cells.Where(c => c.ProjectId == projectId && c.Rerun).ExecuteUpdateAsync(c => c.SetProperty(e => e.Rerun, false), cancellationToken);
        }
        foreach (var stage in stages)
        {
            runner.Publish(new StageUpdate(projectId, stage, await cells.StateAsync(projectId, stage, cancellationToken)));
        }
    }

    private async Task<Plan> PlanOfAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        if (stage is not (PipelineStage.Script or PipelineStage.Voice or PipelineStage.Storyboard or PipelineStage.References or PipelineStage.Stills))
        {
            throw new InvalidOperationException($"The {stage} stage has no rerun from one cell.");
        }
        CellEntry cell;
        VersionOrigin origin;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            cell = await db.Cells.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == stage && c.Key == key && c.CurrentVersion != null, cancellationToken)
                ?? throw new KeyNotFoundException($"There is nothing made for {key} yet.");
            origin = (await db.CellVersions.AsNoTracking().FirstAsync(v => v.ProjectId == projectId && v.Stage == stage && v.Key == key && v.Version == cell.CurrentVersion, cancellationToken)).Origin;
        }
        var shots = await cells.CurrentShotsAsync(projectId, cancellationToken);
        var stills = await cells.CurrentStillsAsync(projectId, cancellationToken);
        var stillsMade = await cells.StateAsync(projectId, PipelineStage.Stills, cancellationToken) != StageState.NotStarted;
        var keep = origin == VersionOrigin.Edited;
        string Label(string shotKey) => ShotTiming.Label(shots, shotKey);
        var plan = stage switch
        {
            PipelineStage.Script or PipelineStage.Voice => await SegmentPlanAsync(projectId, stage, key, keep && stage == PipelineStage.Script, shots, stills, stillsMade, cancellationToken),
            PipelineStage.Storyboard => ShotPlan(key, keep, stills, stillsMade, Label),
            PipelineStage.Stills => StillPlan(key, stills, Label),
            _ => await ImagePlanAsync(projectId, cell, shots, stills, stillsMade, cancellationToken),
        };
        if (!plan.Nothing && plan.Stills.Count > 0)
        {
            // One run makes the whole batch: what you queued yourself goes with it.
            var waiting = (await cells.QueuedStillsAsync(projectId, cancellationToken)).Keys.Count(k => !plan.Stills.Contains(k));
            if (waiting > 0)
            {
                plan = plan with
                {
                    Text = plan.Text + (waiting == 1 ? " The still waiting in the batch is made in the same run." : $" The {waiting} stills waiting in the batch are made in the same run."),
                };
            }
        }
        return plan;
    }

    private async Task<Plan> SegmentPlanAsync(
        Guid projectId, PipelineStage stage, string key, bool keep, IReadOnlyList<Shot> shots, IReadOnlyDictionary<string, Still> stills, bool stillsMade,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var spoken = stage == PipelineStage.Voice;
        if (stage == PipelineStage.Script)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            spoken = await db.Cells.AsNoTracking().AnyAsync(c => c.ProjectId == projectId && c.Stage == PipelineStage.Voice && c.Key == key && c.CurrentVersion != null, cancellationToken);
            if (spoken)
            {
                parts.Add("its voice");
            }
        }
        var own = shots.Where(s => s.SegmentId == key).Select(s => s.Key).ToHashSet();
        var remade = new List<string>();
        var tail = "";
        if (spoken && own.Count > 0)
        {
            parts.Add(own.Count == 1 ? "its shot" : $"its {own.Count} shots");
            if (stillsMade)
            {
                parts.Add(own.Count == 1 ? "its still" : $"their {own.Count} stills");
                var after = stills.Where(s => !own.Contains(s.Key) && !s.Value.YourFile && s.Value.ContinuesFrom is { } from && own.Contains(from)).Select(s => s.Key).ToList();
                if (after.Count > 0)
                {
                    parts.Add(after.Count == 1 ? "the still after them that starts from one" : $"the {after.Count} stills after them that start from one");
                }
                remade = [.. own, .. after];
                if (own.Any(k => stills.GetValueOrDefault(k) is { YourFile: true }))
                {
                    tail += " Your own pictures of its shots go with the old shots.";
                }
            }
            tail += " It waits at each gate for your approval, as a run does.";
        }
        var what = stage == PipelineStage.Script ? "script" : "voice";
        if (keep && parts.Count == 0)
        {
            return new Plan($"Nothing is made from {key}'s {what} yet.", true, [], true);
        }
        var head = keep ? $"This keeps your edit of {key}'s {what} and remakes {Join(parts)}" : $"This remakes {Join([$"{key}'s {what}", .. parts])}";
        return new Plan($"{head}. Other segments stay as they are.{tail}", keep, remade, false);
    }

    private static Plan ShotPlan(string key, bool keep, IReadOnlyDictionary<string, Still> stills, bool stillsMade, Func<string, string> label)
    {
        var parts = new List<string>();
        var remade = new List<string>();
        var mine = stills.GetValueOrDefault(key);
        if (stillsMade && mine is not { YourFile: true })
        {
            parts.Add("its still");
            parts.AddRange(After(key, stills, label));
            remade = [key, .. stills.Where(s => !s.Value.YourFile && s.Value.ContinuesFrom == key).Select(s => s.Key)];
        }
        if (keep && parts.Count == 0)
        {
            return new Plan($"Nothing is made from shot {label(key)} yet.", true, [], true);
        }
        var head = keep ? $"This keeps your changes to shot {label(key)} and remakes {Join(parts)}" : $"This remakes {Join([$"shot {label(key)}", .. parts])}";
        var yours = mine is { YourFile: true } ? " Your own picture of it stays." : "";
        return new Plan($"{head}. Other shots stay as they are.{yours}", keep, remade, false);
    }

    private static Plan StillPlan(string key, IReadOnlyDictionary<string, Still> stills, Func<string, string> label)
    {
        var after = stills.Where(s => !s.Value.YourFile && s.Value.ContinuesFrom == key).Select(s => s.Key).ToList();
        if (stills.GetValueOrDefault(key) is not { YourFile: true })
        {
            return new Plan(
                $"This remakes {Join([$"the still of {label(key)}", .. After(key, stills, label)])}. Other stills stay as they are.",
                false, [key, .. after], false);
        }
        // Your own picture stays; what starts from it is made again.
        return after.Count == 0
            ? new Plan($"Your own picture of {label(key)} stays, and no still starts from it: nothing is made again.", true, [], true)
            : new Plan($"This keeps your own picture of {label(key)} and remakes {Join([.. After(key, stills, label)])}. Other stills stay as they are.", true, after, false);
    }

    private async Task<Plan> ImagePlanAsync(
        Guid projectId, CellEntry cell, IReadOnlyList<Shot> shots, IReadOnlyDictionary<string, Still> stills, bool stillsMade, CancellationToken cancellationToken)
    {
        var (referenceId, _) = ReferencesStage.Parse(cell.Key);
        var name = (await cells.CurrentReferencesAsync(projectId, cancellationToken))?.References.FirstOrDefault(r => r.Id == referenceId)?.Name ?? referenceId;
        if (!stillsMade)
        {
            return new Plan($"The stills are not made yet: they are made from this image of {name} when the pipeline gets there.", true, [], true);
        }
        var direct = PipelineRunner.Redo(PipelineStage.References, new HashSet<string> { cell.Key }, stills, shots.Select(s => s.Key).ToHashSet());
        var first = stills.Count(s => direct.Contains(s.Key) && s.Value.References.Contains(cell.Key));
        if (direct.Count == 0)
        {
            return new Plan($"No still uses this image of {name}: nothing is made again.", true, [], true);
        }
        var text = first == 1 ? $"This remakes the still that uses this image of {name}" : $"This remakes the {first} stills that use this image of {name}";
        if (direct.Count > first)
        {
            text += direct.Count - first == 1 ? " and the one that starts from them" : $" and the {direct.Count - first} that start from them";
        }
        var wait = cell.State != StageState.Approved ? " They are made once you approve the image." : "";
        return new Plan($"{text}. Other stills stay as they are.{wait}", true, [.. direct], false);
    }

    /// <summary>"the still of S04·3, which starts from it": the stills after this one's that start from it.</summary>
    private static IEnumerable<string> After(string key, IReadOnlyDictionary<string, Still> stills, Func<string, string> label) =>
        stills.Where(s => !s.Value.YourFile && s.Value.ContinuesFrom == key).Select(s => $"the still of {label(s.Key)}, which starts from it");

    /// <summary>"a, b and c".</summary>
    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}
