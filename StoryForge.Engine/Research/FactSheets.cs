using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;

namespace StoryForge.Engine.Research;

/// <summary>
/// The fact sheet screen's reads and your changes to facts. Changes collect in one edited version
/// until you approve it; a generated or approved version is never changed.
/// </summary>
internal sealed class FactSheets(IDbContextFactory<StoryForgeDbContext> contextFactory, PipelineRunner runner, TimeProvider clock)
{
    private const PipelineStage Stage = PipelineStage.Research;

    public async Task<FactSheetView> GetAsync(Guid projectId, int? version, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var view = await ViewAsync(db, projectId, version, cancellationToken);
        // While it runs, the activity lives in the runner; the cell gets it when the run ends.
        return runner.LiveActivity(projectId, Stage) is { } live ? view with { Activity = live } : view;
    }

    public async Task<FactSheetView> ChangeAsync(Guid projectId, int version, string factId, FactChange change, CancellationToken cancellationToken)
    {
        var statement = change.Statement?.Trim();
        if (statement is { Length: 0 })
        {
            throw new ArgumentException("A fact needs a statement.");
        }
        if (change.Weight is { } weight && weight is < Fact.MinWeight or > Fact.MaxWeight)
        {
            throw new ArgumentException($"A weight goes from {Fact.MinWeight} to {Fact.MaxWeight}, not {weight}.");
        }
        if (runner.IsBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The research is running. Change facts when it is done.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cell = await db.Cells.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} has no fact sheet yet.");
        var versions = await db.CellVersions
            .Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "")
            .ToListAsync(cancellationToken);
        var source = versions.FirstOrDefault(v => v.Version == version)
            ?? throw new KeyNotFoundException($"The fact sheet has no version {version}.");
        var sheet = StoredJson.Read<FactSheet>(source.OutputJson);
        var fact = sheet.Facts.FirstOrDefault(f => f.Id == factId)
            ?? throw new KeyNotFoundException($"Version {version} has no fact {factId}.");
        var changed = fact with
        {
            Statement = statement ?? fact.Statement,
            Weight = change.Weight ?? fact.Weight,
            LeftOut = change.LeftOut ?? fact.LeftOut,
        };
        if (changed == fact)
        {
            return await ViewAsync(db, projectId, version, cancellationToken);
        }
        var output = StoredJson.Write(new FactSheet([.. sheet.Facts.Select(f => f.Id == factId ? changed : f)]));

        // Your edits collect in the latest version while it is yours and not approved; anything else
        // stays as it was, and the change starts a new version.
        var latest = versions.Max(v => v.Version);
        var target = source;
        if (source.Origin == VersionOrigin.Edited && source.Version == latest && cell.ApprovedVersion != source.Version)
        {
            source.OutputJson = output;
        }
        else
        {
            target = new CellVersionEntry
            {
                ProjectId = projectId,
                Stage = Stage,
                Version = latest + 1,
                Origin = VersionOrigin.Edited,
                BasedOn = source.Version,
                InputHash = source.InputHash,
                SchemaVersion = FactSheet.SchemaVersion,
                OutputJson = output,
                CreatedAt = clock.GetUtcNow(),
            };
            db.CellVersions.Add(target);
        }

        // A changed sheet needs your approval again before the script may use it.
        cell.CurrentVersion = target.Version;
        if (cell.State is StageState.Approved or StageState.Failed)
        {
            cell.State = StageState.NeedsReview;
            cell.Error = null;
        }
        cell.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        runner.Publish(new StageUpdate(projectId, Stage, cell.State));
        return await ViewAsync(db, projectId, target.Version, cancellationToken);
    }

    private static async Task<FactSheetView> ViewAsync(StoryForgeDbContext db, Guid projectId, int? version, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new KeyNotFoundException($"There is no project {projectId}.");
        }
        var cell = await db.Cells.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Stage == Stage && c.Key == "", cancellationToken);
        if (cell is null)
        {
            return new FactSheetView(projectId, StageState.NotStarted, null, [], null, null, null, []);
        }
        var versions = await db.CellVersions.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.Stage == Stage && v.Key == "")
            .OrderBy(v => v.Version)
            .ToListAsync(cancellationToken);
        var shown = version ?? cell.CurrentVersion;
        var entry = shown is null ? null : versions.FirstOrDefault(v => v.Version == shown)
            ?? throw new KeyNotFoundException($"The fact sheet has no version {shown}.");
        return new FactSheetView(
            projectId,
            cell.State,
            cell.State == StageState.Failed ? cell.Error : null,
            [.. versions.Select(v => new ResultVersion(v.Version, v.Origin, v.CreatedAt, v.BasedOn))],
            entry?.Version,
            cell.ApprovedVersion,
            entry is null ? null : StoredJson.Read<FactSheet>(entry.OutputJson),
            StoredJson.Read<List<ActivityLine>>(cell.ActivityJson));
    }
}
