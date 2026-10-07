using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine.Projects;

/// <summary>
/// Projects and the choices they were started with. The setup is stored whole, with the exact
/// profile versions, so a rerun months later reproduces it.
/// </summary>
internal sealed class ProjectStore(IDbContextFactory<StoryForgeDbContext> contextFactory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly HashSet<string> Aspects = ["16:9", "9:16", "1:1"];

    public async Task<IReadOnlyList<ProjectSummary>> ListRecentAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projects = await db.Projects.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.UpdatedAt })
            .ToListAsync(cancellationToken);
        // Sorted here: SQLite cannot order by a DateTimeOffset column.
        return [.. projects.OrderByDescending(p => p.UpdatedAt).Select(p => new ProjectSummary(p.Id, p.Name, StatusLine()))];
    }

    public async Task<Project> CreateAsync(ProjectSetup setup, CancellationToken cancellationToken)
    {
        setup = Normalize(setup);
        Validate(setup);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RequireProfilesAsync(db, setup, cancellationToken);

        var now = clock.GetUtcNow();
        var entry = new ProjectEntry
        {
            Id = Guid.NewGuid(),
            Name = setup.Name,
            CreatedAt = now,
            UpdatedAt = now,
            SetupJson = JsonSerializer.Serialize(setup, Json),
        };
        db.Projects.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return ToProject(entry);
    }

    public async Task<Project> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"There is no project {projectId}.");
        return ToProject(entry);
    }

    // Nothing runs yet; the stages report their own states from #5 on.
    private static string StatusLine() => "Not started";

    private static Project ToProject(ProjectEntry entry) => new(
        entry.Id,
        entry.CreatedAt,
        JsonSerializer.Deserialize<ProjectSetup>(entry.SetupJson, Json)
            ?? throw new InvalidOperationException($"Project {entry.Id} has no setup."),
        [.. Enum.GetValues<PipelineStage>().Select(stage => new StageStatus(stage, StageState.NotStarted))]);

    private static ProjectSetup Normalize(ProjectSetup s) => s with
    {
        Name = (s.Name ?? "").Trim(),
        Brief = (s.Brief ?? "").Trim(),
        ResearchSources = [.. (s.ResearchSources ?? []).Select(r => r.Trim()).Where(r => r.Length > 0)],
        Writing = s.Writing with { Provider = s.Writing.Provider.Trim(), Model = s.Writing.Model.Trim() },
        Voice = s.Voice with { Provider = s.Voice.Provider.Trim(), Model = s.Voice.Model.Trim() },
        Stills = s.Stills with { Provider = s.Stills.Provider.Trim(), Model = s.Stills.Model.Trim() },
        Clips = s.Clips with { Provider = s.Clips.Provider.Trim(), Model = s.Clips.Model.Trim() },
        Output = s.Output with { Aspect = s.Output.Aspect.Trim(), Language = s.Output.Language.Trim() },
        // The required gates are always on, whatever the caller sent; kept in run order.
        Gates = [.. (s.Gates ?? []).Concat(ProjectSetup.RequiredGates).Distinct().Order()],
    };

    private static void Validate(ProjectSetup s)
    {
        Require(s.Name.Length > 0, "A project needs a name.");
        Require(s.Brief.Length > 0, "A project needs a brief.");
        Require(Aspects.Contains(s.Output.Aspect), $"The aspect must be 16:9, 9:16 or 1:1, not \"{s.Output.Aspect}\".");
        Require(s.Output.Width >= 1 && s.Output.Height >= 1, "The delivery resolution must be at least 1 × 1.");
        Require(s.Output.TargetSeconds >= 1, "The target length must be at least 1 second.");
        Require(s.Stills.Size.Width >= 1 && s.Stills.Size.Height >= 1, "The still image size must be at least 1 × 1.");
        Require(s.Clips.Size.Width >= 1 && s.Clips.Size.Height >= 1, "The video clip size must be at least 1 × 1.");
        Require(s.Clips.MaxClipSeconds >= 1, "The max clip length must be at least 1 second.");
    }

    /// <summary>Every profile the project names exists in that exact version, and is of the kind its slot needs.</summary>
    private static async Task RequireProfilesAsync(StoryForgeDbContext db, ProjectSetup s, CancellationToken cancellationToken)
    {
        (ProfileRef Ref, ProfileKind Kind)[] slots =
        [
            (s.Writing.Research, ProfileKind.Research),
            (s.Writing.Script, ProfileKind.Script),
            (s.Writing.Storyboard, ProfileKind.Storyboard),
            (s.Voice.Profile, ProfileKind.Voice),
            (s.Stills.Profile, ProfileKind.Image),
            (s.Clips.Profile, ProfileKind.Video),
        ];
        foreach (var (profileRef, kind) in slots)
        {
            var label = kind.ToString().ToLowerInvariant();
            Require(profileRef is not null, $"Pick a {label} profile.");
            var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileRef!.ProfileId, cancellationToken);
            Require(profile is not null, $"The {label} profile does not exist.");
            Require(profile!.Kind == kind, $"\"{profile.Name}\" is a {profile.Kind.ToString().ToLowerInvariant()} profile, not a {label} profile.");
            var versionExists = await db.ProfileVersions.AnyAsync(
                v => v.ProfileId == profileRef!.ProfileId && v.Version == profileRef.Version, cancellationToken);
            Require(versionExists, $"\"{profile.Name}\" has no version {profileRef!.Version}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
