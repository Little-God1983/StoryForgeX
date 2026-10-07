using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Secrets;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine;

/// <summary>The client the desktop app uses while engine and UI share one process.</summary>
internal sealed class InProcessStoryForgeClient(
    SettingsStore settings,
    ISecretStore secrets,
    ProviderChecks checks,
    ProfileStore profiles,
    ProjectStore projects,
    IOptions<StoryForgeEngineOptions> options) : IStoryForgeClient
{
    public Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default) =>
        checks.CheckAllAsync(cancellationToken);

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        projects.ListRecentAsync(cancellationToken);

    public Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        settings.LoadAsync(cancellationToken);

    public Task SaveSettingsAsync(EngineSettings value, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(value, cancellationToken);

    public async Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default)
    {
        var saved = (await settings.LoadAsync(cancellationToken)).Paths.ProjectsFolder;
        return string.IsNullOrWhiteSpace(saved) ? options.Value.EffectiveDefaultProjectsFolder : saved;
    }

    public Task<bool> HasSecretAsync(SecretKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrEmpty(secrets.Read(SecretName(key))));

    public Task SetSecretAsync(SecretKey key, string? value, CancellationToken cancellationToken = default)
    {
        secrets.Write(SecretName(key), value);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        profiles.ListAsync(cancellationToken);

    public Task<ProfileSummary> CreateProfileAsync(ProfileKind kind, string name, CancellationToken cancellationToken = default) =>
        profiles.CreateAsync(kind, name, cancellationToken);

    public Task<ProfileVersion> GetProfileVersionAsync(Guid profileId, int? version = null, CancellationToken cancellationToken = default) =>
        profiles.GetVersionAsync(profileId, version, cancellationToken);

    public Task<ProfileVersion> SaveProfileVersionAsync(Guid profileId, ProfileContent content, CancellationToken cancellationToken = default) =>
        profiles.SaveVersionAsync(profileId, content, cancellationToken);

    public Task<string> ImportReferenceFileAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        profiles.ImportReferenceFileAsync(sourcePath, cancellationToken);

    public Task<IReadOnlyList<string>> GetWorkflowTemplatesAsync(CancellationToken cancellationToken = default) =>
        profiles.GetWorkflowTemplatesAsync(cancellationToken);

    public Task<Project> CreateProjectAsync(ProjectSetup setup, CancellationToken cancellationToken = default) =>
        projects.CreateAsync(setup, cancellationToken);

    public Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        projects.GetAsync(projectId, cancellationToken);

    internal static string SecretName(SecretKey key) => key switch
    {
        SecretKey.LmStudioApiToken => "lm-studio-api-token",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
