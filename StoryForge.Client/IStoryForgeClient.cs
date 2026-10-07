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

    /// <summary>All profiles, grouped by <see cref="ProfileKind"/> order, then by name.</summary>
    Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a profile with the starting content for its kind, saved as v1.</summary>
    /// <exception cref="ArgumentException">The name is empty or already used by a profile of this kind.</exception>
    Task<ProfileSummary> CreateProfileAsync(ProfileKind kind, string name, CancellationToken cancellationToken = default);

    /// <summary>One version of a profile; null means the latest.</summary>
    /// <exception cref="KeyNotFoundException">No such profile or version.</exception>
    Task<ProfileVersion> GetProfileVersionAsync(Guid profileId, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="content"/> as the next version; earlier versions stay as they are.
    /// Content equal to the latest version saves nothing and returns that version.
    /// </summary>
    Task<ProfileVersion> SaveProfileVersionAsync(Guid profileId, ProfileContent content, CancellationToken cancellationToken = default);

    /// <summary>Copies a reference file into the engine's store; the returned path goes into a profile.</summary>
    Task<string> ImportReferenceFileAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>The workflow files (*.json) in the ComfyUI templates folder, by name; empty if none is set.</summary>
    Task<IReadOnlyList<string>> GetWorkflowTemplatesAsync(CancellationToken cancellationToken = default);
}
