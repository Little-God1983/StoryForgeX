using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Data;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.References;
using StoryForge.Engine.Voice;

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
        var cells = (await db.Cells.AsNoTracking()
                .Where(c => c.Key == "")
                .Select(c => new { c.ProjectId, c.Stage, c.State })
                .ToListAsync(cancellationToken))
            .ToLookup(c => c.ProjectId, c => new StageStatus(c.Stage, c.State));
        // Sorted here: SQLite cannot order by a DateTimeOffset column.
        return [.. projects.OrderByDescending(p => p.UpdatedAt).Select(p => new ProjectSummary(p.Id, p.Name, StatusLine(Stages(cells[p.Id]))))];
    }

    public async Task<Project> CreateAsync(ProjectSetup setup, CancellationToken cancellationToken)
    {
        RequireParts(setup);
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
        return ToProject(entry, []);
    }

    public async Task<Project> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"There is no project {projectId}.");
        var cells = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Key == "")
            .Select(c => new StageStatus(c.Stage, c.State))
            .ToListAsync(cancellationToken);
        return ToProject(entry, cells);
    }

    /// <summary>
    /// A failed stage on the newest version of the profile it started with. Only that stage's slot
    /// moves (references and stills may share one image profile), and only to a version that can run.
    /// </summary>
    public async Task<Project> UseNewerProfileAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"There is no project {projectId}.");
        var setup = ToProject(entry, []).Setup;
        var own = stage switch
        {
            PipelineStage.Voice => setup.Voice.Profile,
            PipelineStage.References => setup.References.Profile,
            PipelineStage.Stills => setup.Stills.Profile,
            PipelineStage.Clips => setup.Clips.Profile,
            _ => throw new ArgumentException($"{stage} has no ComfyUI profile to move.", nameof(stage)),
        };
        var state = await db.Cells.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Stage == stage && c.Key == "")
            .Select(c => (StageState?)c.State)
            .FirstOrDefaultAsync(cancellationToken);
        if (state != StageState.Failed)
        {
            throw new InvalidOperationException($"{stage} has not failed: it keeps the profile it started with.");
        }
        var newest = await db.ProfileVersions.AsNoTracking().Where(v => v.ProfileId == own.ProfileId).MaxAsync(v => (int?)v.Version, cancellationToken);
        if (newest is not { } version || version <= own.Version)
        {
            throw new InvalidOperationException($"The {stage} profile has no version newer than {own.Version}.");
        }
        var newer = own with { Version = version };
        var moved = stage switch
        {
            PipelineStage.Voice => setup with { Voice = setup.Voice with { Profile = newer } },
            PipelineStage.References => setup with { References = setup.References with { Profile = newer } },
            PipelineStage.Stills => setup with { Stills = setup.Stills with { Profile = newer } },
            _ => setup with { Clips = setup.Clips with { Profile = newer } },
        };
        await RequireRunnableAsync(db, moved, cancellationToken);
        entry.SetupJson = JsonSerializer.Serialize(moved, Json);
        entry.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(projectId, cancellationToken);
    }

    /// <summary>
    /// "Not started", or the first stage that is not approved yet and where it stands: "Research: needs
    /// review". When that stage has not started, the last approved one: "Research: approved".
    /// </summary>
    internal static string StatusLine(IReadOnlyList<StageStatus> stages)
    {
        if (stages.All(s => s.State == StageState.NotStarted))
        {
            return "Not started";
        }
        var open = stages.FirstOrDefault(s => s.State != StageState.Approved);
        if (open is null)
        {
            return "All stages approved";
        }
        if (open.State == StageState.NotStarted && stages.TakeWhile(s => s != open).LastOrDefault() is { } approved)
        {
            return $"{approved.Stage}: approved";
        }
        return $"{open.Stage}: {StateText(open.State)}";
    }

    internal static string StateText(StageState state) => state switch
    {
        StageState.NotStarted => "not started",
        StageState.NeedsReview => "needs review",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>Every stage in run order, with the state of its cell, or not started if it has none.</summary>
    private static List<StageStatus> Stages(IEnumerable<StageStatus> cells)
    {
        var states = cells.ToDictionary(c => c.Stage, c => c.State);
        return [.. Enum.GetValues<PipelineStage>().Select(stage => new StageStatus(stage, states.GetValueOrDefault(stage, StageState.NotStarted)))];
    }

    private static Project ToProject(ProjectEntry entry, IEnumerable<StageStatus> cells) => new(
        entry.Id,
        entry.CreatedAt,
        Upgrade(JsonNode.Parse(entry.SetupJson))?.Deserialize<ProjectSetup>(Json)
            ?? throw new InvalidOperationException($"Project {entry.Id} has no setup."),
        Stages(cells));

    /// <summary>
    /// A setup saved before Research, Script and Image references were cards of their own (#59) in
    /// today's shape: research and script take the shared provider and model, the references what
    /// the still images had, with the consistency and the list review that were stored there.
    /// </summary>
    internal static JsonNode? Upgrade(JsonNode? setup)
    {
        if (setup is not JsonObject json || json["Writing"] is not JsonObject writing || json.ContainsKey("Research"))
        {
            return setup;
        }
        json["Research"] = new JsonObject
        {
            ["Provider"] = writing["Provider"]?.DeepClone(),
            ["Model"] = writing["Model"]?.DeepClone(),
            ["Profile"] = writing["Research"]?.DeepClone(),
        };
        json["Script"] = new JsonObject
        {
            ["Provider"] = writing["Provider"]?.DeepClone(),
            ["Model"] = writing["Model"]?.DeepClone(),
            ["Script"] = writing["Script"]?.DeepClone(),
            ["Storyboard"] = writing["Storyboard"]?.DeepClone(),
        };
        if (json["Stills"] is JsonObject stills)
        {
            json["References"] = new JsonObject
            {
                ["Provider"] = stills["Provider"]?.DeepClone(),
                ["Model"] = stills["Model"]?.DeepClone(),
                ["Profile"] = stills["Profile"]?.DeepClone(),
                ["Consistency"] = stills["Consistency"]?.DeepClone() ?? nameof(Consistency.ReferenceImages),
                // Saved before the list review existed: it is reviewed, as every new project is unless it says otherwise.
                ["ReviewList"] = json["ReviewReferenceList"]?.DeepClone() ?? true,
            };
            stills.Remove("Consistency");
        }
        json.Remove("Writing");
        json.Remove("ReviewReferenceList");
        return json;
    }

    private static ProjectSetup Normalize(ProjectSetup s) => s with
    {
        Name = (s.Name ?? "").Trim(),
        Brief = (s.Brief ?? "").Trim(),
        ResearchSources = [.. (s.ResearchSources ?? []).Select(r => (r ?? "").Trim()).Where(r => r.Length > 0)],
        Research = s.Research with { Provider = Tidy(s.Research.Provider), Model = Tidy(s.Research.Model) },
        Script = s.Script with { Provider = Tidy(s.Script.Provider), Model = Tidy(s.Script.Model) },
        Voice = s.Voice with { Provider = Tidy(s.Voice.Provider), Model = Tidy(s.Voice.Model) },
        References = s.References with { Provider = Tidy(s.References.Provider), Model = Tidy(s.References.Model) },
        Stills = s.Stills with { Provider = Tidy(s.Stills.Provider), Model = Tidy(s.Stills.Model) },
        Clips = s.Clips with { Provider = Tidy(s.Clips.Provider), Model = Tidy(s.Clips.Model) },
        Output = s.Output with { Aspect = Tidy(s.Output.Aspect), Language = Tidy(s.Output.Language) },
        // The required gates are always on, whatever the caller sent; kept in run order.
        Gates = [.. (s.Gates ?? []).Concat(ProjectSetup.RequiredGates).Distinct().Order()],
    };

    private static string Tidy(string? text) => (text ?? "").Trim();

    private static double Ratio(string aspect) => aspect switch
    {
        "16:9" => 16.0 / 9,
        "9:16" => 9.0 / 16,
        _ => 1,
    };

    // Generation sizes follow the model, not the exact ratio (1344 × 768 for 16:9): only the shape counts.
    private static bool HasShape(GenerationSize size, string aspect) => aspect switch
    {
        "16:9" => size.Width > size.Height,
        "9:16" => size.Width < size.Height,
        _ => size.Width == size.Height,
    };

    /// <summary>
    /// Every part is there before anything reads it, so a setup with a part missing (from a remote
    /// caller, later) is an ArgumentException as documented, not a NullReferenceException.
    /// </summary>
    private static void RequireParts(ProjectSetup s)
    {
        Require(s is not null, "A project needs a setup.");
        Require(s!.Research is not null, "The research choices are missing.");
        Require(s.Script is not null, "The script choices are missing.");
        Require(s.Voice is not null, "The voice choices are missing.");
        Require(s.References is not null, "The image reference choices are missing.");
        Require(s.Stills is not null && s.Stills.Size is not null, "The still image choices are missing.");
        Require(s.Clips is not null && s.Clips.Size is not null, "The video clip choices are missing.");
        Require(s.Output is not null, "The output choices are missing.");
    }

    private static void Validate(ProjectSetup s)
    {
        Require(s.Name.Length > 0, "A project needs a name.");
        Require(s.Brief.Length > 0, "A project needs a brief.");
        Require(Aspects.Contains(s.Output.Aspect), $"The aspect must be 16:9, 9:16 or 1:1, not \"{s.Output.Aspect}\".");
        Require(s.Output.Width >= 1 && s.Output.Height >= 1, "The delivery resolution must be at least 1 × 1.");
        // Delivery sizes are exact (1920 × 1080); 2 % leaves room for rounding, not for another shape.
        Require(Math.Abs((double)s.Output.Width / s.Output.Height / Ratio(s.Output.Aspect) - 1) <= 0.02,
            $"A {s.Output.Width} × {s.Output.Height} delivery does not fit a {s.Output.Aspect} video.");
        Require(s.Output.Language.Length > 0, "A project needs a language.");
        Require(Enum.IsDefined(s.Output.Assembly), $"There is no assembly \"{s.Output.Assembly}\".");
        Require(Enum.IsDefined(s.References.Consistency), $"There is no consistency \"{s.References.Consistency}\".");
        Require(Enum.IsDefined(s.Mode), $"There is no run mode \"{s.Mode}\".");
        Require(s.Gates.All(Enum.IsDefined), "A gate names a stage that does not exist.");
        Require(s.Output.TargetSeconds >= 1, "The target length must be at least 1 second.");
        Require(s.Stills.Size.Width >= 1 && s.Stills.Size.Height >= 1, "The still image size must be at least 1 × 1.");
        Require(s.Clips.Size.Width >= 1 && s.Clips.Size.Height >= 1, "The video clip size must be at least 1 × 1.");
        Require(s.Clips.MaxClipSeconds >= 1, "The max clip length must be at least 1 second.");
        Require(s.Pacing?.Problem(s.Clips.MaxClipSeconds) is null, s.Pacing?.Problem(s.Clips.MaxClipSeconds) ?? "");
        // Stills and clips are generated in the video's shape; a 16:9 still in a 9:16 video is a mistake.
        Require(s.Stills.Size.Aspect == s.Output.Aspect && HasShape(s.Stills.Size, s.Output.Aspect),
            $"A {s.Stills.Size.Width} × {s.Stills.Size.Height} still does not fit a {s.Output.Aspect} video.");
        Require(s.Clips.Size.Aspect == s.Output.Aspect && HasShape(s.Clips.Size, s.Output.Aspect),
            $"A {s.Clips.Size.Width} × {s.Clips.Size.Height} clip does not fit a {s.Output.Aspect} video.");
    }

    /// <summary>Every profile the project names exists in that exact version, and is of the kind its slot needs.</summary>
    private static async Task RequireProfilesAsync(StoryForgeDbContext db, ProjectSetup s, CancellationToken cancellationToken)
    {
        (ProfileRef Ref, ProfileKind Kind)[] slots =
        [
            (s.Research.Profile, ProfileKind.Research),
            (s.Script.Script, ProfileKind.Script),
            (s.Script.Storyboard, ProfileKind.Storyboard),
            (s.Voice.Profile, ProfileKind.Voice),
            (s.References.Profile, ProfileKind.Image),
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
        await RequireRunnableAsync(db, s, cancellationToken);
    }

    /// <summary>
    /// The ComfyUI stages' profiles can run: each names a workflow and says where the values its stage
    /// needs go. The setup never changes once started (bar a newer version of the same profile), so one
    /// that cannot would be found out only after research, script and storyboard.
    /// </summary>
    internal static async Task RequireRunnableAsync(StoryForgeDbContext db, ProjectSetup s, CancellationToken cancellationToken)
    {
        var (voice, voiceProfile) = await ContentAsync(db, s.Voice.Profile, cancellationToken);
        RequireWorkflow(voice, voiceProfile, "speak the script", "Voice", "voice");
        Require(WorkflowTemplate.Maps(voiceProfile.Inputs, VoiceStage.TextKey),
            $"\"{voice}\" cannot speak the script: it does not say where the text goes. Map '{VoiceStage.TextKey}' in the profile, or pick another voice profile.");
        if (s.References.Consistency == Consistency.ReferenceImages)
        {
            var (name, reference) = await ContentAsync(db, s.References.Profile, cancellationToken);
            RequireWorkflow(name, reference, "make reference images", "Image", "reference");
            Require(WorkflowTemplate.Maps(reference.Inputs, ReferencesStage.PromptKey),
                $"\"{name}\" cannot make reference images: it does not say where the prompt goes. Map '{ReferencesStage.PromptKey}' in the profile, or pick another reference profile.");
            Require(WorkflowTemplate.Maps(reference.Inputs, ReferencesStage.ImageKey) || WorkflowTemplate.Maps(reference.Inputs, ReferencesStage.ImagesKey),
                $"\"{name}\" cannot make reference images: it does not say where the picture goes. Map '{ReferencesStage.ImageKey}' (or '{ReferencesStage.ImagesKey}') in the profile, or pick another reference profile.");
        }
        var (still, stillProfile) = await ContentAsync(db, s.Stills.Profile, cancellationToken);
        RequireWorkflow(still, stillProfile, "make stills", "Image", "still");
        Require(WorkflowTemplate.Maps(stillProfile.Inputs, ReferencesStage.PromptKey),
            $"\"{still}\" cannot make stills: it does not say where the prompt goes. Map '{ReferencesStage.PromptKey}' in the profile, or pick another still profile.");
        Require(s.References.Consistency != Consistency.ReferenceImages
            || WorkflowTemplate.Maps(stillProfile.Inputs, ReferencesStage.ImageKey) || WorkflowTemplate.Maps(stillProfile.Inputs, ReferencesStage.ImagesKey),
            $"\"{still}\" cannot make stills from the reference images: it does not say where they go. Map '{ReferencesStage.ImageKey}' (or '{ReferencesStage.ImagesKey}') in the profile, pick another still profile, or make the stills from text only.");
        var (video, videoProfile) = await ContentAsync(db, s.Clips.Profile, cancellationToken);
        RequireWorkflow(video, videoProfile, "make clips", "Video", "video");
        // What ClipsStage needs to put the still, its prompt and its length into the workflow.
        foreach (var key in new[] { ReferencesStage.PromptKey, ReferencesStage.ImageKey, ClipsStage.FramesKey })
        {
            Require(WorkflowTemplate.Maps(videoProfile.Inputs, key),
                $"\"{video}\" cannot make clips: it does not say where '{key}' goes. Map it in the profile, or pick another video profile.");
        }
    }

    /// <summary>"…cannot make clips: it has no workflow. Pick one in Profiles → Video, or pick another video profile."</summary>
    private static void RequireWorkflow(string name, ProfileContent profile, string task, string page, string slot) =>
        Require(!string.IsNullOrWhiteSpace(profile.WorkflowTemplate),
            $"\"{name}\" cannot {task}: it has no workflow. Pick one in Profiles → {page}, or pick another {slot} profile.");

    /// <summary>A profile version's name and content.</summary>
    private static async Task<(string Name, ProfileContent Content)> ContentAsync(StoryForgeDbContext db, ProfileRef profile, CancellationToken cancellationToken)
    {
        var name = (await db.Profiles.AsNoTracking().FirstAsync(p => p.Id == profile.ProfileId, cancellationToken)).Name;
        var json = await db.ProfileVersions.AsNoTracking()
            .Where(v => v.ProfileId == profile.ProfileId && v.Version == profile.Version)
            .Select(v => v.Json)
            .FirstAsync(cancellationToken);
        return (name, ProfileStore.Read(json));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
