namespace StoryForge.Client;

/// <summary>
/// Everything the app can ask of the engine. The UI talks to the engine only through this
/// interface: in-process today, over HTTP + SignalR once the engine can run on a server.
/// </summary>
public interface IStoryForgeClient
{
    /// <summary>Checks every provider now and returns their states, in <see cref="ProviderId"/> order.</summary>
    Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>The projects listed under "Recent projects", most recent first.</summary>
    Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved settings, or the defaults for anything never saved.</summary>
    Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(EngineSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The projects folder actually in use: the saved one, or the engine's default.</summary>
    Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default);

    Task<bool> HasSecretAsync(SecretKey key, CancellationToken cancellationToken = default);

    /// <summary>Stores a secret in the OS credential store; null or empty removes it.</summary>
    Task SetSecretAsync(SecretKey key, string? value, CancellationToken cancellationToken = default);
}
