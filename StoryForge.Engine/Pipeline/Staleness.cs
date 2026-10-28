using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Pipeline;

/// <summary>How many stills a change to a reference reaches, for the question before it.</summary>
/// <param name="Using">By reference id: the stills its images went into, and those starting from one of them.</param>
/// <param name="Showing">By reference id: every still of a shot it is in, pictured or in words, and those starting from one.</param>
/// <param name="UsingImage">By image key ("R01/2"): the stills that image went into, and those starting from one.</param>
internal sealed record StillCounts(IReadOnlyDictionary<string, int> Using, IReadOnlyDictionary<string, int> Showing, IReadOnlyDictionary<string, int> UsingImage);

/// <summary>
/// Which results are out of date (stale), and why: a cell is stale when what it would be made from
/// now differs from what it was made from (its version's input hash, or for a shot the voice it was
/// cut from), or when something it was made from is stale itself. What each stage would use now is
/// what it reads when it runs: a segment's approved version, else its current one; a still, the
/// approved reference images. Nothing is stored: it is worked out when a screen asks (and kept until
/// something of the project changes), so a result made again is up to date at once.
/// </summary>
/// <remarks>
/// The chain: the fact sheet → script segments → their voices → their shots → their stills; and
/// reference images → the stills that use them.
/// </remarks>
internal sealed class Staleness(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    CellReader cells,
    ProjectStore projects,
    ProfileStore profiles,
    ProjectFolders folders)
{
    private sealed record Result(object Stamp, IReadOnlyDictionary<(PipelineStage Stage, string Key), string> Stale, StillCounts Counts);

    // The last answer per project: one matrix load asks four times, and every live update loads again.
    private readonly ConcurrentDictionary<Guid, Result> _last = new();

    /// <summary>Every stale cell of the project, by stage and key, with the reason its panel shows.</summary>
    public async Task<IReadOnlyDictionary<(PipelineStage Stage, string Key), string>> GetAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await ResultAsync(projectId, cancellationToken)).Stale;

    /// <summary>How many stills each reference (and each of its images) reaches: what a change to it would put out of date.</summary>
    public async Task<StillCounts> CountsAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await ResultAsync(projectId, cancellationToken)).Counts;

    private async Task<Result> ResultAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Every change to a cell touches its time, and every new version adds one.
        object stamp;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // SQLite cannot take the latest of a date with its offset: the few cells come here.
            var changed = (await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => c.UpdatedAt).ToListAsync(cancellationToken))
                .DefaultIfEmpty().Max();
            var count = await db.CellVersions.AsNoTracking().CountAsync(v => v.ProjectId == projectId, cancellationToken);
            stamp = (changed, count);
        }
        if (_last.TryGetValue(projectId, out var last) && last.Stamp.Equals(stamp))
        {
            return last;
        }
        Result result;
        try
        {
            result = await ComputeAsync(projectId, stamp, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The marks are a help, not the screen: what cannot be worked out is left off.
            Debug.WriteLine($"Working out what of {projectId} is out of date failed: {ex}");
            return new Result(stamp, new Dictionary<(PipelineStage, string), string>(),
                new StillCounts(new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>()));
        }
        _last[projectId] = result;
        return result;
    }

    private async Task<Result> ComputeAsync(Guid projectId, object stamp, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var setup = project.Setup;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var all = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Key != "" && c.CurrentVersion != null).ToListAsync(cancellationToken);
        // Only the versions a cell points to (its current and its approved one), by stage, key and version.
        var wanted = all.SelectMany(c => new[] { c.CurrentVersion, c.ApprovedVersion }.OfType<int>().Select(v => (c.Stage, c.Key, v))).ToHashSet();
        var versions = (await (
                from v in db.CellVersions.AsNoTracking()
                join c in db.Cells.AsNoTracking() on new { v.ProjectId, v.Stage, v.Key } equals new { c.ProjectId, c.Stage, c.Key }
                where v.ProjectId == projectId && v.Key != "" && (v.Version == c.CurrentVersion || v.Version == c.ApprovedVersion)
                select v).ToListAsync(cancellationToken))
            .Where(v => wanted.Contains((v.Stage, v.Key, v.Version)))
            .ToDictionary(v => (v.Stage, v.Key, v.Version));
        CellVersionEntry? Current(CellEntry cell, Func<CellEntry, int?> pick) =>
            pick(cell) is { } number && versions.TryGetValue((cell.Stage, cell.Key, number), out var v) ? v : null;
        IEnumerable<CellEntry> Of(PipelineStage stage) => all.Where(c => c.Stage == stage);
        var stale = new Dictionary<(PipelineStage, string), string>();

        // What a cell shows is its current version; what the next stage was made from is the one it
        // reads, its approved version else its current one. Out of date is passed down from that one.
        static int? Read(CellEntry cell) => cell.ApprovedVersion ?? cell.CurrentVersion;
        var readStale = new HashSet<(PipelineStage, string)>();

        // The script, from the approved fact sheet.
        if ((await cells.ApprovedFactSheetAsync(projectId, cancellationToken))?.Version is { } facts)
        {
            var hash = ScriptStage.Hash(setup, facts);
            foreach (var cell in Of(PipelineStage.Script))
            {
                if (Current(cell, c => c.CurrentVersion) is { } version && version.InputHash != hash)
                {
                    stale[(PipelineStage.Script, cell.Key)] = "The fact sheet changed since it was written.";
                }
                if (Current(cell, Read) is { } read && read.InputHash != hash)
                {
                    readStale.Add((PipelineStage.Script, cell.Key));
                }
            }
        }

        // A segment's voice, from the segment as the voice reads it.
        var script = Of(PipelineStage.Script).ToDictionary(c => c.Key, c => Current(c, Read));
        foreach (var cell in Of(PipelineStage.Voice))
        {
            if (script.GetValueOrDefault(cell.Key) is not { } segment)
            {
                continue;
            }
            var hash = VoiceStage.Hash(setup, StoredJson.Read<Segment>(segment.OutputJson), segment.Version);
            var scriptStale = readStale.Contains((PipelineStage.Script, cell.Key));
            if (Current(cell, c => c.CurrentVersion) is { } version)
            {
                if (version.InputHash != hash)
                {
                    stale[(PipelineStage.Voice, cell.Key)] = $"{cell.Key}'s script changed since it was spoken.";
                }
                else if (scriptStale)
                {
                    stale[(PipelineStage.Voice, cell.Key)] = $"{cell.Key}'s script is out of date.";
                }
            }
            if (Current(cell, Read) is { } read && (read.InputHash != hash || scriptStale))
            {
                readStale.Add((PipelineStage.Voice, cell.Key));
            }
        }

        // A shot, from the voice of its segment it was cut to.
        var board = await cells.CurrentStoryboardAsync(projectId, cancellationToken);
        var voices = Of(PipelineStage.Voice).ToDictionary(c => c.Key, c => c.ApprovedVersion ?? c.CurrentVersion);
        var shots = new Dictionary<string, Shot>();
        foreach (var cell in Of(PipelineStage.Storyboard))
        {
            if (Current(cell, c => c.CurrentVersion) is not { } version)
            {
                continue;
            }
            var shot = StoredJson.Read<Shot>(version.OutputJson);
            shots[cell.Key] = shot;
            if (board?.VoiceVersions.TryGetValue(shot.SegmentId, out var cutTo) != true)
            {
                continue;
            }
            if (voices.GetValueOrDefault(shot.SegmentId) is { } now && now != cutTo)
            {
                stale[(PipelineStage.Storyboard, cell.Key)] = $"{shot.SegmentId}'s voice changed since the shot was cut.";
            }
            else if (readStale.Contains((PipelineStage.Voice, shot.SegmentId)))
            {
                stale[(PipelineStage.Storyboard, cell.Key)] = $"{shot.SegmentId}'s voice is out of date.";
            }
        }
        var ordered = shots.Values.OrderBy(s => s.SegmentId, StringComparer.Ordinal).ThenBy(s => s.FirstWord).ToList();
        var labels = shots.ToDictionary(s => s.Key, s => ShotTiming.Label(ordered, s.Key));

        // A reference image, from its reference on the list: the version shown on the card, and the
        // approved one the stills are made from.
        var sheet = await cells.CurrentReferencesAsync(projectId, cancellationToken);
        var names = (sheet?.References ?? []).ToDictionary(r => r.Id, r => r.Name);
        var staleForStills = new HashSet<string>();
        foreach (var cell in Of(PipelineStage.References))
        {
            var (referenceId, _) = ReferencesStage.Parse(cell.Key);
            if (sheet?.References.FirstOrDefault(r => r.Id == referenceId) is not { } reference)
            {
                continue;
            }
            var hash = ReferencesStage.Hash(setup, reference);
            if (Current(cell, c => c.CurrentVersion) is { } version && version.InputHash != hash)
            {
                stale[(PipelineStage.References, cell.Key)] = $"{reference.Name}'s look or pictures changed since this image was made.";
            }
            if (Current(cell, c => c.ApprovedVersion) is { } approved && approved.InputHash != hash)
            {
                staleForStills.Add(cell.Key);
            }
        }

        // A still, from its shot, the reference images in it, the words heard and the still before.
        var stills = new Dictionary<string, (Still Still, CellVersionEntry Version)>();
        foreach (var cell in Of(PipelineStage.Stills))
        {
            if (Current(cell, c => c.CurrentVersion) is { } version)
            {
                stills[cell.Key] = (StoredJson.Read<Still>(version.OutputJson), version);
            }
        }
        IReadOnlyDictionary<string, string> hashes;
        try
        {
            hashes = await StillsStage.CurrentHashesAsync(cells, folders, profiles, project, stills.ToDictionary(s => s.Key, s => s.Value.Still), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The marks are a help, not the screen: one that cannot be worked out is left off.
            Debug.WriteLine($"Working out which stills of {projectId} are out of date failed: {ex}");
            hashes = new Dictionary<string, string>();
        }
        // In shot order, so a still that starts from the one before sees whether that one is stale.
        foreach (var key in ordered.Select(s => s.Key))
        {
            if (!stills.TryGetValue(key, out var made))
            {
                continue;
            }
            var label = labels[key];
            var why = stale.ContainsKey((PipelineStage.Storyboard, key)) ? $"Shot {label} is out of date."
                : made.Still.References.FirstOrDefault(staleForStills.Contains) is { } image
                    ? $"{names.GetValueOrDefault(ReferencesStage.Parse(image).ReferenceId, image)}'s image is out of date."
                : made.Still.ContinuesFrom is { } from && stale.ContainsKey((PipelineStage.Stills, from)) ? $"The still of {labels.GetValueOrDefault(from, from)} it starts from is out of date."
                : hashes.TryGetValue(key, out var hash) && hash != made.Version.InputHash ? Changed(made, label)
                : null;
            if (why is not null)
            {
                stale[(PipelineStage.Stills, key)] = why;
            }
        }
        // A clip, from the approved still it starts from and the length of its shot.
        foreach (var cell in Of(PipelineStage.Clips))
        {
            if (Current(cell, c => c.CurrentVersion) is not { } version)
            {
                continue;
            }
            var clip = StoredJson.Read<Clip>(version.OutputJson);
            var label = labels.GetValueOrDefault(cell.Key, cell.Key);
            var still = Of(PipelineStage.Stills).FirstOrDefault(c => c.Key == cell.Key);
            var why = still?.ApprovedVersion is { } approved && approved != clip.StillVersion ? $"The still of {label} changed since the clip was made."
                : stale.ContainsKey((PipelineStage.Stills, cell.Key)) ? $"The still of {label} is out of date."
                : stale.ContainsKey((PipelineStage.Storyboard, cell.Key)) ? $"Shot {label} is out of date."
                : shots.TryGetValue(cell.Key, out var shot) && Math.Abs(clip.Seconds - 2 * clip.Handle - shot.Seconds) > 0.01 ? $"Shot {label} was cut to another length since the clip was made."
                : null;
            if (why is not null)
            {
                stale[(PipelineStage.Clips, cell.Key)] = why;
            }
        }
        return new Result(stamp, stale, Counts(ordered, stills.ToDictionary(s => s.Key, s => s.Value.Still), sheet?.References ?? []));

        // What changed since, as far as the dates tell: the still before, the shot, an image in it, or else something it was made from.
        string Changed((Still Still, CellVersionEntry Version) made, string label)
        {
            var still = made.Version;
            if (made.Still.ContinuesFrom is { } from && stills.TryGetValue(from, out var before) && before.Version.CreatedAt > still.CreatedAt)
            {
                return $"The still of {labels.GetValueOrDefault(from, from)} it starts from was made again since.";
            }
            if (Of(PipelineStage.Storyboard).FirstOrDefault(c => c.Key == still.Key) is { } shotCell && Current(shotCell, c => c.CurrentVersion) is { } shot && shot.CreatedAt > still.CreatedAt)
            {
                return $"Shot {label} changed since the still was made.";
            }
            foreach (var key in made.Still.References)
            {
                if (Of(PipelineStage.References).FirstOrDefault(c => c.Key == key) is { } imageCell && Current(imageCell, c => c.ApprovedVersion) is { } approved && approved.CreatedAt > still.CreatedAt)
                {
                    return $"{names.GetValueOrDefault(ReferencesStage.Parse(key).ReferenceId, key)}'s image changed since the still was made.";
                }
            }
            return "What it was made from changed since: the shot's words, a look, or the still before.";
        }
    }

    /// <summary>The stills each reference and each image reaches, continuations of them too, in shot order.</summary>
    private static StillCounts Counts(IReadOnlyList<Shot> shots, IReadOnlyDictionary<string, Still> stills, IReadOnlyList<Reference> references)
    {
        Dictionary<string, int> Reach(IEnumerable<string> ids, Func<string, Shot, Still, bool> direct)
        {
            var counts = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                var reached = new HashSet<string>();
                foreach (var shot in shots)
                {
                    // Your own picture is made of nothing: a reference change leaves it as it is.
                    if (stills.GetValueOrDefault(shot.Key) is { YourFile: false } still
                        && (direct(id, shot, still) || (still.ContinuesFrom is { } from && reached.Contains(from))))
                    {
                        reached.Add(shot.Key);
                    }
                }
                if (reached.Count > 0)
                {
                    counts[id] = reached.Count;
                }
            }
            return counts;
        }
        var byId = references.ToDictionary(r => r.Id);
        return new StillCounts(
            Reach(byId.Keys, (id, _, still) => still.References.Any(k => k.StartsWith(id + "/", StringComparison.Ordinal))),
            Reach(byId.Keys, (id, shot, _) => shot.Cast.Any(n => ReferenceList.IsNamed(byId[id], n))),
            Reach(stills.Values.SelectMany(s => s.References).Distinct(), (key, _, still) => still.References.Contains(key)));
    }
}
