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

/// <summary>
/// Which results are out of date (stale), and why: a cell is stale when what it would be made from
/// now differs from what it was made from (its version's input hash, or for a shot the voice it was
/// cut from), or when something it was made from is stale itself. What each stage would use now is
/// what it reads when it runs: a segment's approved version, else its current one. Nothing is
/// stored: it is worked out when a screen asks, so a result made again is up to date at once.
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
    /// <summary>Every stale cell of the project, by stage and key, with the reason its panel shows.</summary>
    public async Task<IReadOnlyDictionary<(PipelineStage Stage, string Key), string>> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var setup = project.Setup;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var all = await db.Cells.AsNoTracking().Where(c => c.ProjectId == projectId && c.Key != "" && c.CurrentVersion != null).ToListAsync(cancellationToken);
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Key != "")
            .Select(v => new { v.Stage, v.Key, v.Version, v.InputHash, v.CreatedAt, v.OutputJson })
            .ToListAsync(cancellationToken);
        CellVersionEntry? Current(CellEntry cell, Func<CellEntry, int?> pick) =>
            versions.FirstOrDefault(v => v.Stage == cell.Stage && v.Key == cell.Key && v.Version == pick(cell)) is { } v
                ? new CellVersionEntry { Stage = v.Stage, Key = v.Key, Version = v.Version, InputHash = v.InputHash, CreatedAt = v.CreatedAt, OutputJson = v.OutputJson }
                : null;
        IEnumerable<CellEntry> Of(PipelineStage stage) => all.Where(c => c.Stage == stage);
        var stale = new Dictionary<(PipelineStage, string), string>();

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
            }
        }

        // A segment's voice, from the segment as the voice reads it.
        var script = Of(PipelineStage.Script).ToDictionary(c => c.Key, c => Current(c, s => s.ApprovedVersion ?? s.CurrentVersion));
        foreach (var cell in Of(PipelineStage.Voice))
        {
            if (Current(cell, c => c.CurrentVersion) is not { } version || script.GetValueOrDefault(cell.Key) is not { } segment)
            {
                continue;
            }
            if (version.InputHash != VoiceStage.Hash(setup, StoredJson.Read<Segment>(segment.OutputJson), segment.Version))
            {
                stale[(PipelineStage.Voice, cell.Key)] = $"{cell.Key}'s script changed since it was spoken.";
            }
            else if (stale.ContainsKey((PipelineStage.Script, cell.Key)))
            {
                stale[(PipelineStage.Voice, cell.Key)] = $"{cell.Key}'s script is out of date.";
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
            else if (stale.ContainsKey((PipelineStage.Voice, shot.SegmentId)))
            {
                stale[(PipelineStage.Storyboard, cell.Key)] = $"{shot.SegmentId}'s voice is out of date.";
            }
        }
        var labels = shots.ToDictionary(s => s.Key, s => ShotTiming.Label(shots.Values, s.Key));

        // A reference image, from its reference on the list.
        var sheet = await cells.CurrentReferencesAsync(projectId, cancellationToken);
        var names = (sheet?.References ?? []).ToDictionary(r => r.Id, r => r.Name);
        foreach (var cell in Of(PipelineStage.References))
        {
            var (referenceId, _) = ReferencesStage.Parse(cell.Key);
            if (Current(cell, c => c.CurrentVersion) is { } version && sheet?.References.FirstOrDefault(r => r.Id == referenceId) is { } reference
                && version.InputHash != ReferencesStage.Hash(setup, reference))
            {
                stale[(PipelineStage.References, cell.Key)] = $"{reference.Name}'s look or pictures changed since this image was made.";
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
        var hashes = await StillsStage.CurrentHashesAsync(cells, folders, profiles, project, stills.ToDictionary(s => s.Key, s => s.Value.Still), cancellationToken);
        // In shot order, so a still that starts from the one before sees whether that one is stale.
        foreach (var key in shots.Values.OrderBy(s => s.SegmentId, StringComparer.Ordinal).ThenBy(s => s.FirstWord).Select(s => s.Key))
        {
            if (!stills.TryGetValue(key, out var made))
            {
                continue;
            }
            var label = labels[key];
            var references = made.Still.References.Select(k => ReferencesStage.Parse(k).ReferenceId).Distinct().ToList();
            var why = stale.ContainsKey((PipelineStage.Storyboard, key)) ? $"Shot {label} is out of date."
                : made.Still.References.FirstOrDefault(k => stale.ContainsKey((PipelineStage.References, k))) is { } image
                    ? $"{names.GetValueOrDefault(ReferencesStage.Parse(image).ReferenceId, image)}'s image is out of date."
                : made.Still.ContinuesFrom is { } from && stale.ContainsKey((PipelineStage.Stills, from)) ? $"The still of {labels.GetValueOrDefault(from, from)} it starts from is out of date."
                : hashes.TryGetValue(key, out var hash) && hash != made.Version.InputHash ? Changed(made, label, references, names)
                : null;
            if (why is not null)
            {
                stale[(PipelineStage.Stills, key)] = why;
            }
        }
        return stale;

        // What changed since, as far as the dates tell: the shot, an image of someone in it, or else something it was made from.
        string Changed((Still Still, CellVersionEntry Version) made, string label, IReadOnlyList<string> references, IReadOnlyDictionary<string, string> names)
        {
            var still = made.Version;
            var key = still.Key;
            if (made.Still.ContinuesFrom is { } from && stills.TryGetValue(from, out var before) && before.Version.CreatedAt > still.CreatedAt)
            {
                return $"The still of {labels.GetValueOrDefault(from, from)} it starts from was made again since.";
            }
            if (Of(PipelineStage.Storyboard).FirstOrDefault(c => c.Key == key) is { } shotCell && Current(shotCell, c => c.CurrentVersion) is { } shot && shot.CreatedAt > still.CreatedAt)
            {
                return $"Shot {label} changed since the still was made.";
            }
            foreach (var id in references)
            {
                var newer = Of(PipelineStage.References)
                    .Where(c => c.Key.StartsWith(id + "/", StringComparison.Ordinal))
                    .Select(c => Current(c, i => i.ApprovedVersion))
                    .Any(v => v is not null && v.CreatedAt > still.CreatedAt);
                if (newer)
                {
                    return $"{names.GetValueOrDefault(id, id)}'s image changed since the still was made.";
                }
            }
            return "What it was made from changed since: the shot's words, a look, or the still before.";
        }
    }

    /// <summary>How many stills use each reference, by its id: what changing its images would put out of date.</summary>
    public async Task<IReadOnlyDictionary<string, int>> StillsUsingAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await cells.CurrentStillsAsync(projectId, cancellationToken)).Values
            .SelectMany(s => s.References.Select(k => ReferencesStage.Parse(k).ReferenceId).Distinct())
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
}
