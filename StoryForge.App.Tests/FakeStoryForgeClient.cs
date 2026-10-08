using System.IO;
using System.Text.Json;
using StoryForge.Client;

namespace StoryForge.App.Tests;

internal sealed class FakeStoryForgeClient : IStoryForgeClient
{
    public List<ProviderStatus> Providers { get; } = [];

    public List<ProjectSummary> RecentProjects { get; } = [];

    public EngineSettings Settings { get; set; } = EngineSettings.Defaults;

    public Dictionary<SecretKey, string> Secrets { get; } = [];

    public int StatusChecks { get; private set; }

    public int Saves { get; private set; }

    public string DefaultProjectsFolder { get; set; } = @"C:\Data\projects";

    /// <summary>When set, SaveSettingsAsync throws it instead of saving.</summary>
    public Exception? SaveFailure { get; set; }

    /// <summary>When set, provider checks wait for it, like slow real checks.</summary>
    public Task? StatusGate { get; set; }

    /// <summary>When set, provider checks throw it.</summary>
    public Exception? StatusFailure { get; set; }

    public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default)
    {
        StatusChecks++;
        if (StatusFailure is not null)
        {
            throw StatusFailure;
        }
        if (StatusGate is not null)
        {
            await StatusGate;
        }
        return [.. Providers];
    }

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        RecentProjectsFailure is not null
            ? Task.FromException<IReadOnlyList<ProjectSummary>>(RecentProjectsFailure)
            : Task.FromResult<IReadOnlyList<ProjectSummary>>([.. RecentProjects]);

    /// <summary>When set, reading the settings throws it.</summary>
    public Exception? SettingsLoadFailure { get; set; }

    public Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SettingsLoadFailure is not null ? Task.FromException<EngineSettings>(SettingsLoadFailure) : Task.FromResult(Settings);

    /// <summary>When set, saves wait for it, so tests can see whether two overlap.</summary>
    public Task? SaveGate { get; set; }

    public int MostSavesAtOnce { get; private set; }

    private int _savesRunning;

    public async Task SaveSettingsAsync(EngineSettings settings, CancellationToken cancellationToken = default)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }
        MostSavesAtOnce = Math.Max(MostSavesAtOnce, ++_savesRunning);
        try
        {
            if (SaveGate is not null)
            {
                await SaveGate;
            }
            Saves++;
            Settings = settings;
        }
        finally
        {
            _savesRunning--;
        }
    }

    /// <summary>When set, asking for the projects folder in use throws it.</summary>
    public Exception? EffectiveFolderFailure { get; set; }

    public Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default) =>
        EffectiveFolderFailure is not null ? Task.FromException<string>(EffectiveFolderFailure) : Task.FromResult(Settings.Paths.ProjectsFolder is { Length: > 0 } folder ? folder : DefaultProjectsFolder);

    /// <summary>When set, reading whether a secret exists throws it.</summary>
    public Exception? SecretFailure { get; set; }

    public Task<bool> HasSecretAsync(SecretKey key, CancellationToken cancellationToken = default) =>
        SecretFailure is not null ? Task.FromException<bool>(SecretFailure) : Task.FromResult(Secrets.ContainsKey(key));

    public Task SetSecretAsync(SecretKey key, string? value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value))
        {
            Secrets.Remove(key);
        }
        else
        {
            Secrets[key] = value;
        }
        return Task.CompletedTask;
    }
    public static ProfileContent EmptyContent => ProfileContent.Empty;

    /// <summary>What a new profile of each kind starts with; tests replace it to suit them.</summary>
    public Func<ProfileKind, ProfileContent> Starter { get; set; } = kind => kind switch
    {
        ProfileKind.Image => EmptyContent with
        {
            Provider = "ComfyUI",
            PromptTemplate = "{shot.visual}",
            Inputs = [new("prompt", ""), new("seed", "")],
            Sizes = [new("16:9", 1344, 768), new("9:16", 768, 1344)],
        },
        ProfileKind.Video => EmptyContent with { Provider = "ComfyUI", Sizes = [new("16:9", 1280, 720)], MaxClipSeconds = 20 },
        ProfileKind.Voice => EmptyContent with { Provider = "ComfyUI", Inputs = [new("text", "")] },
        _ => EmptyContent with { Provider = "Claude CLI", Instructions = "Write it." },
    };

    private readonly List<(ProfileSummary Summary, List<ProfileVersion> Versions)> _profiles = [];

    public List<string> WorkflowTemplates { get; } = [];

    public int TemplateReads { get; private set; }

    public int ProfileSaves { get; private set; }

    /// <summary>When set, profile saves wait for it.</summary>
    public Task? ProfileSaveGate { get; set; }

    /// <summary>When set, profile saves throw it.</summary>
    public Exception? ProfileSaveFailure { get; set; }

    /// <summary>When set, loading a profile version throws it.</summary>
    public Exception? ProfileLoadFailure { get; set; }

    /// <summary>When set, loading a profile version waits for it.</summary>
    public Task? ProfileLoadGate { get; set; }

    /// <summary>When set, listing the profiles throws it.</summary>
    public Exception? ProfileListFailure { get; set; }

    public List<string> ImportedFiles { get; } = [];

    public Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        ProfileListFailure is not null
            ? Task.FromException<IReadOnlyList<ProfileSummary>>(ProfileListFailure)
            : Task.FromResult<IReadOnlyList<ProfileSummary>>(
                [.. _profiles.Select(p => p.Summary).OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)]);

    public Task<ProfileSummary> CreateProfileAsync(ProfileKind kind, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0 || _profiles.Any(p => p.Summary.Kind == kind && p.Summary.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromException<ProfileSummary>(new ArgumentException($"There is already a profile called \"{name}\"."));
        }
        var summary = new ProfileSummary(Guid.NewGuid(), kind, name, 1);
        _profiles.Add((summary, [new ProfileVersion(summary.Id, 1, DateTimeOffset.UtcNow, Starter(kind))]));
        return Task.FromResult(summary);
    }

    /// <summary>Adds a profile with the given versions, as if saved in an earlier session.</summary>
    public ProfileSummary AddProfile(ProfileKind kind, string name, params ProfileContent[] versions)
    {
        var summary = new ProfileSummary(Guid.NewGuid(), kind, name, versions.Length);
        _profiles.Add((summary, [.. versions.Select((c, i) => new ProfileVersion(summary.Id, i + 1, DateTimeOffset.UtcNow, c))]));
        return summary;
    }

    public async Task<ProfileVersion> GetProfileVersionAsync(Guid profileId, int? version = null, CancellationToken cancellationToken = default)
    {
        if (ProfileLoadFailure is not null)
        {
            throw ProfileLoadFailure;
        }
        if (ProfileLoadGate is not null)
        {
            await ProfileLoadGate;
        }
        var versions = _profiles.SingleOrDefault(p => p.Summary.Id == profileId).Versions ?? throw new KeyNotFoundException();
        return (version is null ? versions[^1] : versions.SingleOrDefault(v => v.Version == version)) ?? throw new KeyNotFoundException();
    }

    public async Task<ProfileVersion> SaveProfileVersionAsync(Guid profileId, ProfileContent content, CancellationToken cancellationToken = default)
    {
        if (ProfileSaveFailure is not null)
        {
            throw ProfileSaveFailure;
        }
        if (ProfileSaveGate is not null)
        {
            await ProfileSaveGate;
        }
        var index = _profiles.FindIndex(p => p.Summary.Id == profileId);
        var (summary, versions) = _profiles[index];
        if (JsonSerializer.Serialize(versions[^1].Content) == JsonSerializer.Serialize(content))
        {
            return versions[^1];
        }
        ProfileSaves++;
        var saved = new ProfileVersion(profileId, versions.Count + 1, DateTimeOffset.UtcNow, content);
        versions.Add(saved);
        _profiles[index] = (summary with { LatestVersion = saved.Version }, versions);
        return saved;
    }

    /// <summary>File names whose import fails.</summary>
    public HashSet<string> FailingImports { get; } = [];

    public Task<string> ImportReferenceFileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (FailingImports.Contains(Path.GetFileName(sourcePath)))
        {
            return Task.FromException<string>(new IOException("access denied"));
        }
        ImportedFiles.Add(sourcePath);
        return Task.FromResult(@"C:\Data\references\" + Path.GetFileName(sourcePath));
    }

    /// <summary>When true, every template read waits until a test completes it from <see cref="PendingTemplateReads"/>.</summary>
    public bool HoldTemplateReads { get; set; }

    public Queue<TaskCompletionSource<IReadOnlyList<string>>> PendingTemplateReads { get; } = [];

    /// <summary>The templates folder in the saved settings at each template read.</summary>
    public List<string> TemplateReadFolders { get; } = [];

    public Task<IReadOnlyList<string>> GetWorkflowTemplatesAsync(CancellationToken cancellationToken = default)
    {
        TemplateReads++;
        TemplateReadFolders.Add(Settings.ComfyUi.WorkflowTemplatesFolder);
        if (HoldTemplateReads)
        {
            var read = new TaskCompletionSource<IReadOnlyList<string>>();
            PendingTemplateReads.Enqueue(read);
            return read.Task;
        }
        return Task.FromResult<IReadOnlyList<string>>([.. WorkflowTemplates]);
    }
    public List<Project> Projects { get; } = [];

    /// <summary>When set, creating a project throws it.</summary>
    public Exception? ProjectCreateFailure { get; set; }

    /// <summary>When set, reading the recent projects throws it.</summary>
    public Exception? RecentProjectsFailure { get; set; }

    public Task<Project> CreateProjectAsync(ProjectSetup setup, CancellationToken cancellationToken = default)
    {
        if (ProjectCreateFailure is not null)
        {
            return Task.FromException<Project>(ProjectCreateFailure);
        }
        var project = new Project(Guid.NewGuid(), DateTimeOffset.UtcNow, setup,
            [.. Enum.GetValues<PipelineStage>().Select(stage => new StageStatus(stage, StageState.NotStarted))]);
        Projects.Add(project);
        RecentProjects.Insert(0, new ProjectSummary(project.Id, setup.Name, "Not started"));
        return Task.FromResult(project);
    }

    public Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Projects.FirstOrDefault(p => p.Id == projectId) is { } project
            ? Task.FromResult(project)
            : Task.FromException<Project>(new KeyNotFoundException($"There is no project {projectId}."));

    public event EventHandler<StageUpdate>? StageUpdated;

    /// <summary>Raises <see cref="StageUpdated"/> as the engine would, and keeps the project's stage state in step.</summary>
    public void Raise(StageUpdate update)
    {
        var index = Projects.FindIndex(p => p.Id == update.ProjectId);
        if (index >= 0)
        {
            Projects[index] = Projects[index] with
            {
                Stages = [.. Projects[index].Stages.Select(s => s.Stage == update.Stage ? s with { State = update.State } : s)],
            };
        }
        if (FactSheets.TryGetValue(update.ProjectId, out var view) && update.Stage == PipelineStage.Research)
        {
            FactSheets[update.ProjectId] = view with { State = update.State };
        }
        StageUpdated?.Invoke(this, update);
    }

    public List<Guid> StartedRuns { get; } = [];

    public List<(Guid ProjectId, PipelineStage Stage)> Regenerated { get; } = [];

    public List<(Guid ProjectId, PipelineStage Stage)> Cancelled { get; } = [];

    public List<(Guid ProjectId, PipelineStage Stage, int Version)> Approved { get; } = [];

    public List<(Guid ProjectId, int Version, string FactId, FactChange Change)> FactChanges { get; } = [];

    /// <summary>The fact sheet per project; a project without one has an empty, not started sheet.</summary>
    public Dictionary<Guid, FactSheetView> FactSheets { get; } = [];

    /// <summary>When set, the next call that acts on a stage (start, approve, change a fact …) throws it.</summary>
    public Exception? StageFailure { get; set; }

    public Task StartRunAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        StartedRuns.Add(projectId);
        return Task.CompletedTask;
    }

    public Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Regenerated.Add((projectId, stage));
        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default)
    {
        Cancelled.Add((projectId, stage));
        return Task.CompletedTask;
    }

    public Task ApproveAsync(Guid projectId, PipelineStage stage, int version, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Approved.Add((projectId, stage, version));
        if (FactSheets.TryGetValue(projectId, out var view))
        {
            FactSheets[projectId] = view with { State = StageState.Approved, ApprovedVersion = version, Version = version };
        }
        return Task.CompletedTask;
    }

    /// <summary>When set, reading a fact sheet takes what it read now and waits for this before answering.</summary>
    public Task? FactSheetGate { get; set; }

    /// <summary>When set, changing a fact waits for this before it changes anything.</summary>
    public Task? FactChangeGate { get; set; }

    public async Task<FactSheetView> GetFactSheetAsync(Guid projectId, int? version = null, CancellationToken cancellationToken = default)
    {
        var view = ReadFactSheet(projectId, version);
        if (FactSheetGate is not null)
        {
            await FactSheetGate;
        }
        return view;
    }

    private FactSheetView ReadFactSheet(Guid projectId, int? version)
    {
        var view = FactSheets.GetValueOrDefault(projectId) ?? new FactSheetView(projectId, StageState.NotStarted, null, [], null, null, null, []);
        if (version is not null && SheetVersions.TryGetValue((projectId, version.Value), out var older))
        {
            view = view with { Version = version, Sheet = older };
        }
        return view;
    }

    /// <summary>Older versions' sheets, for showing a version other than the current one.</summary>
    public Dictionary<(Guid ProjectId, int Version), FactSheet> SheetVersions { get; } = [];

    /// <summary>Changes the fact in the stored sheet, in the same version (enough to show it on screen).</summary>
    public async Task<FactSheetView> ChangeFactAsync(Guid projectId, int version, string factId, FactChange change, CancellationToken cancellationToken = default)
    {
        if (FactChangeGate is not null)
        {
            await FactChangeGate;
        }
        ThrowIfFailing();
        FactChanges.Add((projectId, version, factId, change));
        var view = FactSheets[projectId];
        var facts = view.Sheet!.Facts.Select(f => f.Id != factId ? f : f with
        {
            Statement = change.Statement ?? f.Statement,
            Weight = change.Weight ?? f.Weight,
            LeftOut = change.LeftOut ?? f.LeftOut,
        });
        FactSheets[projectId] = view = view with { Sheet = new FactSheet([.. facts]) };
        return view;
    }

    /// <summary>The script per project; a project without one has an empty, not started script.</summary>
    public Dictionary<Guid, ScriptView> Scripts { get; } = [];

    public List<(Guid ProjectId, string SegmentId, int Version)> ApprovedSegments { get; } = [];

    public List<Guid> ApprovedScripts { get; } = [];

    public List<(Guid ProjectId, string SegmentId)> RegeneratedSegments { get; } = [];

    public List<(Guid ProjectId, string SegmentId, string Title, string Narration)> EditedSegments { get; } = [];

    public List<(Guid ProjectId, string SegmentId)> CancelledSegments { get; } = [];

    public Task CancelSegmentAsync(Guid projectId, string segmentId, CancellationToken cancellationToken = default)
    {
        CancelledSegments.Add((projectId, segmentId));
        return Task.CompletedTask;
    }

    public List<(Guid ProjectId, string SegmentId, int Version)> SelectedSegmentVersions { get; } = [];

    public Task<ScriptView> GetScriptAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Scripts.GetValueOrDefault(projectId) ?? new ScriptView(projectId, StageState.NotStarted, null, [], [], "", 0, 240, []));

    public Task ApproveSegmentAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        ApprovedSegments.Add((projectId, segmentId, version));
        ChangeSegment(projectId, segmentId, s => s with { State = StageState.Approved, ApprovedVersion = version });
        return Task.CompletedTask;
    }

    public Task ApproveScriptAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        ApprovedScripts.Add(projectId);
        if (Scripts.TryGetValue(projectId, out var view))
        {
            Scripts[projectId] = view with { State = StageState.Approved, Segments = [.. view.Segments.Select(s => s with { State = StageState.Approved, ApprovedVersion = s.Version })] };
        }
        return Task.CompletedTask;
    }

    public Task RegenerateSegmentAsync(Guid projectId, string segmentId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        RegeneratedSegments.Add((projectId, segmentId));
        return Task.CompletedTask;
    }

    public Task EditSegmentAsync(Guid projectId, string segmentId, string title, string narration, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        EditedSegments.Add((projectId, segmentId, title, narration));
        ChangeSegment(projectId, segmentId, s => s with
        {
            Title = title,
            Narration = narration,
            Version = s.Versions.Count + 1,
            State = StageState.NeedsReview,
            Versions = [.. s.Versions, new ResultVersion(s.Versions.Count + 1, VersionOrigin.Edited, DateTimeOffset.UtcNow, s.Version)],
        });
        return Task.CompletedTask;
    }

    public Task SelectSegmentVersionAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        SelectedSegmentVersions.Add((projectId, segmentId, version));
        ChangeSegment(projectId, segmentId, s => s with { Version = version });
        return Task.CompletedTask;
    }

    private void ChangeSegment(Guid projectId, string segmentId, Func<SegmentView, SegmentView> change)
    {
        if (Scripts.TryGetValue(projectId, out var view))
        {
            Scripts[projectId] = view with { Segments = [.. view.Segments.Select(s => s.Id == segmentId ? change(s) : s)] };
        }
    }

    private void ThrowIfFailing()
    {
        if (StageFailure is { } failure)
        {
            StageFailure = null;
            throw failure;
        }
    }
}
