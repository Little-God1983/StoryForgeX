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

    public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default)
    {
        StatusChecks++;
        if (StatusGate is not null)
        {
            await StatusGate;
        }
        return [.. Providers];
    }

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectSummary>>(RecentProjects);

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

    public Task<bool> HasSecretAsync(SecretKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.ContainsKey(key));

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
}
