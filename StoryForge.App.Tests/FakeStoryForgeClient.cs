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

    public Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

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

    public Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings.Paths.ProjectsFolder is { Length: > 0 } folder ? folder : DefaultProjectsFolder);

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
}
