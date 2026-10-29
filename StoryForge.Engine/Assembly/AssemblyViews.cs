using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Assembly;

/// <summary>
/// The Assembly screen's reads, Render again (your cut list, rendered as it is) and Regenerate
/// (Claude writes a new one, and yours is dropped). Approve and Cancel are the runner's, as for any stage.
/// </summary>
internal sealed class AssemblyViews(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    AssemblyTimeline timelines,
    CellReader cells,
    ProjectStore projects,
    ProjectFolders folders,
    Staleness staleness,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Assembly;

    public async Task<AssemblyView> GetAsync(Guid projectId, int? version, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        var root = await folders.RootAsync(cancellationToken);
        var clipsApproved = await cells.StateAsync(projectId, PipelineStage.Clips, cancellationToken) == StageState.Approved;
        IReadOnlyList<AssemblyShot> shots = [];
        if (await cells.CurrentStoryboardAsync(projectId, cancellationToken) is not null)
        {
            try
            {
                var (timeline, _, _) = await timelines.BuildAsync(projectId, cancellationToken);
                shots = [.. timeline.Shots.Select(s => new AssemblyShot(
                    s.Key, s.Label, s.Start, s.End, Math.Round(Math.Min(s.Head, s.Tail), 3), s.Heard,
                    File.Exists(Path.Combine(root, s.ClipFile)) ? Path.Combine(root, s.ClipFile) : null))];
            }
            catch (StageFailedException)
            {
                // A segment not spoken yet: no timeline to show before the clips are there.
            }
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        if (cell is null)
        {
            return new AssemblyView(projectId, StageState.NotStarted, null, [], null, null, [], null, null, shots, clipsApproved, null);
        }
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "")
            .OrderBy(v => v.Version)
            .ToListAsync(cancellationToken);
        var shown = version ?? cell.CurrentVersion;
        var entry = shown is null ? null : versions.FirstOrDefault(v => v.Version == shown)
            ?? throw new KeyNotFoundException($"The Assembly has no version {version}.");
        var video = entry is null ? null : StoredJson.Read<AssembledVideo>(entry.OutputJson);
        var path = video is null ? null : Path.Combine(root, video.File);
        var stale = await staleness.GetAsync(projectId, cancellationToken);
        return new AssemblyView(
            projectId,
            cell.State,
            cell.State == StageState.Failed ? cell.Error : null,
            runner.LiveActivity(projectId, Stage) ?? StoredJson.Read<List<ActivityLine>>(cell.ActivityJson),
            shown,
            cell.ApprovedVersion,
            // Your cut list rendered is your edit of the version it came from.
            [.. versions.Select(v => (v, StoredJson.Read<AssembledVideo>(v.OutputJson).BasedOn)).Select(x =>
                new ResultVersion(x.v.Version, x.BasedOn is null ? VersionOrigin.Generated : VersionOrigin.Edited, x.v.CreatedAt, x.BasedOn))],
            video,
            path is not null && File.Exists(path) ? path : null,
            shots,
            clipsApproved,
            cell.Queued && cell.QueuedPrompt is { } waiting ? StoredJson.Read<WaitingCutList>(waiting).CutList : null)
        {
            Stale = shown == cell.CurrentVersion ? stale.GetValueOrDefault((Stage, "")) : null,
        };
    }

    public async Task RenderAsync(Guid projectId, CutList cutList, int? editedFrom, CancellationToken cancellationToken)
    {
        await projects.GetAsync(projectId, cancellationToken);
        if (runner.IsBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The video is being cut. Try again when it is done.");
        }
        if (await cells.StateAsync(projectId, PipelineStage.Clips, cancellationToken) != StageState.Approved)
        {
            throw new InvalidOperationException("Approve every clip first: the video is cut from them.");
        }
        var (timeline, missing, _) = await timelines.BuildAsync(projectId, cancellationToken);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no approved clip.");
        }
        // The version you edited: a cut you touched there is yours, so the panel can say so; one you left is still Claude's.
        var basis = editedFrom is { } from ? (await VersionAsync(projectId, from, cancellationToken), from) : await cells.CurrentAssemblyAsync(projectId, cancellationToken);
        var before = basis?.Video.CutList.Cuts.ToDictionary(c => c.After);
        var list = cutList with
        {
            Cuts = [.. cutList.Cuts.Select(c => before?.GetValueOrDefault(c.After) is { } was && Same(was, c) ? was : c with { Yours = true })],
            Music = cutList.Music ?? [],
        };
        if (CutPlan.Problems(list, timeline) is { Count: > 0 } problems)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }
        (bool Queued, string? Prompt) was = default;
        await ChangeAsync(projectId, cell =>
        {
            was = (cell.Queued, cell.QueuedPrompt);
            cell.Queued = true;
            cell.QueuedPrompt = StoredJson.Write(new WaitingCutList(list, basis?.Version));
        }, cancellationToken);
        try
        {
            await runner.RegenerateAsync(projectId, Stage, cancellationToken);
        }
        catch
        {
            // Not queued after all (a run slipped in): yours does not wait to be rendered by a later run unasked.
            await ChangeAsync(projectId, cell => (cell.Queued, cell.QueuedPrompt) = was, CancellationToken.None, create: false);
            throw;
        }
    }

    private async Task<AssembledVideo> VersionAsync(Guid projectId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.CellVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "" && v.Version == version, cancellationToken)
            ?? throw new KeyNotFoundException($"The Assembly has no version {version}.");
        return StoredJson.Read<AssembledVideo>(entry.OutputJson);
    }

    /// <summary>Regenerate: Claude writes a new cut list; yours, if one waits, is dropped.</summary>
    public async Task RegenerateAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (runner.IsBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The video is being cut. Try again when it is done.");
        }
        await ChangeAsync(projectId, cell =>
        {
            cell.Queued = false;
            cell.QueuedPrompt = null;
        }, cancellationToken, create: false);
        await runner.RegenerateAsync(projectId, Stage, cancellationToken);
    }

    private static bool Same(Cut a, Cut b) =>
        a.Transition == b.Transition && Near(a.Seconds, b.Seconds) && Near(a.Shift, b.Shift) && Near(a.SoundLead, b.SoundLead);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.0005;

    private async Task ChangeAsync(Guid projectId, Action<CellEntry> change, CancellationToken cancellationToken, bool create = true)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        if (cell is null)
        {
            if (!create)
            {
                return;
            }
            cell = new CellEntry { ProjectId = projectId, Stage = Stage, Key = "", State = StageState.NotStarted };
            db.Cells.Add(cell);
        }
        change(cell);
        cell.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
