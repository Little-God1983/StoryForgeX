using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.Storyboard;

/// <summary>
/// What you change on the storyboard's shots: what a shot shows, who is in it, where it cuts,
/// split and merge. Every shot a change touches gets its next version, to review again (without
/// the gate, approved as it is); a cut moved or a shot split or merged keeps the segment adding up
/// to its audio, and no shot breaks a length limit.
/// </summary>
internal sealed class StoryboardEdits(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    PipelineRunner runner,
    SegmentCells segments,
    CellReader cells,
    ProjectStore projects,
    TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Storyboard;

    public Task EditAsync(Guid projectId, string shotKey, ShotChange change, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, shotKey, board =>
        {
            var shot = board.Shot;
            var description = change.Description?.Trim() ?? shot.Description;
            if (description.Length == 0)
            {
                throw new ArgumentException("A shot needs a description of what is seen.");
            }
            var cast = change.Cast is null ? shot.Cast : StoryboardCheck.CastOf(change.Cast, StoryboardCheck.Names(board.Sheet.Cast.Select(c => c.Name)));
            if (cast.Count > Shot.MaxCast)
            {
                throw new ArgumentException($"A shot takes at most {Shot.MaxCast}: what Minimax takes per clip.");
            }
            var changed = shot with { Description = description, Cast = cast, ContinuesPrevious = change.ContinuesPrevious ?? shot.ContinuesPrevious };
            return changed.Description == shot.Description && changed.Cast.SequenceEqual(shot.Cast) && changed.ContinuesPrevious == shot.ContinuesPrevious
                ? []
                : [Change.Replace(changed)];
        }, cancellationToken);

    /// <summary>
    /// The References timeline: who and what is in the shot, as <paramref name="change"/> makes it
    /// from what it is. The shot keeps its approval: that screen has no Approve for shots.
    /// </summary>
    public Task SetCastAsync(Guid projectId, string shotKey, Func<IReadOnlyList<string>, IReadOnlyList<string>> change, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, shotKey, board =>
        {
            var shot = board.Shot;
            var cast = change(shot.Cast);
            if (cast.SequenceEqual(shot.Cast))
            {
                return [];
            }
            if (cast.Count > Shot.MaxCast)
            {
                throw new ArgumentException($"{board.Label(board.Index)} has {Shot.MaxCast} in it already: what Minimax takes per clip. Take one out first.");
            }
            return [Change.Replace(shot with { Cast = cast })];
        }, cancellationToken, keepApproval: true);

    /// <summary>Moves a shot's start or end by <paramref name="words"/>; the shot on the other side of the cut takes or gives the words.</summary>
    public Task MoveCutAsync(Guid projectId, string shotKey, ShotEdge edge, int words, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, shotKey, board =>
        {
            if (words == 0)
            {
                return [];
            }
            var (index, label) = (board.Index, board.Label(board.Index));
            if (edge == ShotEdge.Start && index == 0)
            {
                throw new ArgumentException($"{label} starts with its segment; that cut does not move.");
            }
            if (edge == ShotEdge.End && index == board.Segment.Count - 1)
            {
                throw new ArgumentException($"{label} ends with its segment; that cut does not move.");
            }
            var before = edge == ShotEdge.Start ? index - 1 : index;
            var (a, b) = (board.Segment[before], board.Segment[before + 1]);
            var cut = b.FirstWord + words;
            if (cut <= a.FirstWord)
            {
                throw new ArgumentException($"{board.Label(before)} would have no words left.");
            }
            if (cut >= b.FirstWord + b.WordCount)
            {
                throw new ArgumentException($"{board.Label(before + 1)} would have no words left.");
            }
            var first = board.Retime(a, a.FirstWord, cut - a.FirstWord, board.Label(before));
            var second = board.Retime(b, cut, b.FirstWord + b.WordCount - cut, board.Label(before + 1));
            return [Change.Replace(first), Change.Replace(second)];
        }, cancellationToken);

    /// <summary>Splits a shot at a word: the new shot starts there, shows the same, and continues the one before.</summary>
    public Task SplitAsync(Guid projectId, string shotKey, int atWord, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, shotKey, board =>
        {
            var shot = board.Shot;
            var label = board.Label(board.Index);
            if (atWord <= shot.FirstWord || atWord >= shot.FirstWord + shot.WordCount)
            {
                throw new ArgumentException($"Word {atWord} is not inside {label}, which is words {shot.FirstWord} to {shot.FirstWord + shot.WordCount - 1}.");
            }
            var first = board.Retime(shot, shot.FirstWord, atWord - shot.FirstWord, label);
            var second = board.Retime(
                shot with { Key = StoryboardCheck.NewKey(shot.SegmentId, board.Keys), ContinuesPrevious = true },
                atWord,
                shot.FirstWord + shot.WordCount - atWord,
                $"{shot.SegmentId}·{board.Index + 2}");
            return [Change.Replace(first), Change.Add(second)];
        }, cancellationToken);

    /// <summary>Merges a shot with the next shot of its segment; the merged shot keeps the first one's description.</summary>
    public Task MergeAsync(Guid projectId, string shotKey, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, shotKey, board =>
        {
            var label = board.Label(board.Index);
            if (board.Index == board.Segment.Count - 1)
            {
                throw new ArgumentException($"{label} is the last shot of {board.Shot.SegmentId}; there is no shot after it to merge with.");
            }
            var next = board.Segment[board.Index + 1];
            var merged = board.Retime(board.Shot, board.Shot.FirstWord, board.Shot.WordCount + next.WordCount, label);
            return [Change.Replace(merged), Change.Remove(next.Key)];
        }, cancellationToken);

    /// <summary>What one edit does to one shot's cell.</summary>
    private sealed record Change(Shot? Shot, string Key, bool IsNew)
    {
        public static Change Replace(Shot shot) => new(shot, shot.Key, false);

        public static Change Add(Shot shot) => new(shot, shot.Key, true);

        public static Change Remove(string key) => new(null, key, false);
    }

    /// <summary>The shot being changed, among the shots of its segment, and the words they are timed by.</summary>
    /// <param name="Keys">Every shot key of the storyboard, so a new shot gets one of its own.</param>
    private sealed record Board(StoryboardSheet Sheet, IReadOnlyList<Shot> Segment, int Index, SegmentWords Words, int MaxClipSeconds, HashSet<string> Keys)
    {
        public Shot Shot => Segment[Index];

        public string Label(int index) => $"{Shot.SegmentId}·{index + 1}";

        /// <summary>The shot over other words, timed from them; refused when it would break a length limit.</summary>
        public Shot Retime(Shot shot, int first, int count, string label)
        {
            var (start, end) = ShotTiming.Span(Words, first, count);
            if (ShotTiming.LengthProblem(label, end - start, Words, MaxClipSeconds, proposed: true) is { } problem)
            {
                throw new ArgumentException(problem);
            }
            return shot with { FirstWord = first, WordCount = count, Start = start, End = end };
        }
    }

    /// <param name="keepApproval">An approved shot stays approved with its new version; otherwise the gate decides.</param>
    private async Task ChangeAsync(Guid projectId, string shotKey, Func<Board, IReadOnlyList<Change>> plan, CancellationToken cancellationToken, bool keepApproval = false)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The storyboard is being planned. Try again when it is done.");
        }
        var setup = (await projects.GetAsync(projectId, cancellationToken)).Setup;
        var sheet = await cells.CurrentStoryboardAsync(projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} has no storyboard yet.");
        var shots = await cells.CurrentShotsAsync(projectId, cancellationToken);
        var shot = shots.FirstOrDefault(s => s.Key == shotKey) ?? throw new KeyNotFoundException($"The storyboard has no shot {shotKey}.");
        var segment = shots.Where(s => s.SegmentId == shot.SegmentId).ToList();
        var clip = sheet.VoiceVersions.TryGetValue(shot.SegmentId, out var version)
            ? await cells.VoiceAsync(projectId, shot.SegmentId, version, cancellationToken)
            : null;
        if (clip is null)
        {
            throw new KeyNotFoundException($"The voice {shot.SegmentId} was cut from is gone.");
        }
        var changes = plan(new Board(sheet, segment, segment.IndexOf(shot), new SegmentWords(shot.SegmentId, clip.Words, clip.Seconds), setup.Clips.MaxClipSeconds, [.. await cells.UsedShotKeysAsync(projectId, cancellationToken)]));
        if (changes.Count == 0)
        {
            return;
        }
        if (changes.Any(c => c.Shot is null || c.IsNew) && (runner.IsAnyBusy(projectId, PipelineStage.Stills) || runner.IsAnyBusy(projectId, PipelineStage.Clips)))
        {
            // A shot merged away meanwhile would get its still (or clip) back from the run, and one split off would be missed.
            throw new InvalidOperationException(runner.IsAnyBusy(projectId, PipelineStage.Stills)
                ? "The stills are being made from the storyboard. Try again when they are done."
                : "The clips are being made from the storyboard. Try again when they are done.");
        }

        var gated = await segments.GatedAsync(projectId, Stage, cancellationToken);
        var states = new List<(string Key, StageState State)>();
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = clock.GetUtcNow();
            foreach (var change in changes)
            {
                var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == change.Key, cancellationToken);
                if (change.Shot is null)
                {
                    if (cell is not null)
                    {
                        db.Cells.Remove(cell);
                        await PipelineRunner.RemoveStillsAsync(db, projectId, [change.Key], cancellationToken);
                        states.Add((change.Key, StageState.NotStarted));
                    }
                    continue;
                }
                CellVersionEntry? current = null;
                if (change.IsNew)
                {
                    cell = new CellEntry { ProjectId = projectId, Stage = Stage, Key = change.Key };
                    db.Cells.Add(cell);
                }
                else
                {
                    cell = cell ?? throw new KeyNotFoundException($"The storyboard has no shot {change.Key}.");
                    current = await db.CellVersions.FirstAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == change.Key && v.Version == cell.CurrentVersion, cancellationToken);
                }
                var next = 1 + (await db.CellVersions.Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == change.Key).MaxAsync(v => (int?)v.Version, cancellationToken) ?? 0);
                db.CellVersions.Add(new CellVersionEntry
                {
                    ProjectId = projectId,
                    Stage = Stage,
                    Key = change.Key,
                    Version = next,
                    Origin = VersionOrigin.Edited,
                    BasedOn = current?.Version,
                    // A new half was made from the same inputs as the shot it was split from.
                    InputHash = current?.InputHash ?? (await db.CellVersions.FirstAsync(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == shotKey, cancellationToken)).InputHash,
                    SchemaVersion = Shot.SchemaVersion,
                    OutputJson = StoredJson.Write(change.Shot),
                    CreatedAt = now,
                });
                var review = gated && !(keepApproval && cell.State == StageState.Approved);
                cell.CurrentVersion = next;
                cell.State = review ? StageState.NeedsReview : StageState.Approved;
                cell.ApprovedVersion = review ? cell.ApprovedVersion : next;
                cell.Error = null;
                cell.UpdatedAt = now;
                states.Add((change.Key, cell.State));
            }
            if (await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) is { } project)
            {
                project.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        foreach (var (key, state) in states)
        {
            runner.Publish(new StageUpdate(projectId, Stage, state, Key: key));
        }
        await runner.SettleAsync(projectId, Stage, cancellationToken);
        await runner.SettleAsync(projectId, PipelineStage.Stills, cancellationToken);   // a shot split off has no still yet
        await runner.SettleAsync(projectId, PipelineStage.Clips, cancellationToken);   // nor a clip
    }
}
