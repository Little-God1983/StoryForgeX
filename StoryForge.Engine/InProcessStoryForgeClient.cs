using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Secrets;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine;

/// <summary>The client the desktop app uses while engine and UI share one process.</summary>
internal sealed class InProcessStoryForgeClient(
    SettingsStore settings,
    ISecretStore secrets,
    ProviderChecks checks,
    ProfileStore profiles,
    ProjectStore projects,
    PipelineRunner runner,
    FactSheets factSheets,
    ScriptSegments script,
    SegmentCells segments,
    VoiceSegments voice,
    ProjectFolders folders) : IStoryForgeClient
{
    public event EventHandler<StageUpdate>? StageUpdated
    {
        add => runner.StageUpdated += value;
        remove => runner.StageUpdated -= value;
    }

    public Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default) =>
        checks.CheckAllAsync(cancellationToken);

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        projects.ListRecentAsync(cancellationToken);

    public Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        settings.LoadAsync(cancellationToken);

    public Task SaveSettingsAsync(EngineSettings value, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(value, cancellationToken);

    public Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default) =>
        folders.RootAsync(cancellationToken);

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

    public Task StartRunAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        runner.StartRunAsync(projectId, cancellationToken);

    public Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default) =>
        runner.RegenerateAsync(projectId, stage, cancellationToken);

    public Task CancelAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default) =>
        runner.CancelAsync(projectId, stage);

    public Task ApproveAsync(Guid projectId, PipelineStage stage, int version, CancellationToken cancellationToken = default) =>
        runner.ApproveAsync(projectId, stage, version, cancellationToken);

    public Task<FactSheetView> GetFactSheetAsync(Guid projectId, int? version = null, CancellationToken cancellationToken = default) =>
        factSheets.GetAsync(projectId, version, cancellationToken);

    public Task<FactSheetView> ChangeFactAsync(Guid projectId, int version, string factId, FactChange change, CancellationToken cancellationToken = default) =>
        factSheets.ChangeAsync(projectId, version, factId, change, cancellationToken);

    public Task<ScriptView> GetScriptAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        script.GetAsync(projectId, cancellationToken);

    public Task ApproveSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken = default) =>
        segments.ApproveAsync(projectId, stage, segmentId, version, cancellationToken);

    public Task ApproveSegmentsAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default) =>
        segments.ApproveAllAsync(projectId, stage, cancellationToken);

    public Task RegenerateSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, CancellationToken cancellationToken = default) =>
        runner.RegenerateSegmentAsync(projectId, stage, segmentId, cancellationToken);

    public Task CancelSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, CancellationToken cancellationToken = default) =>
        runner.CancelAsync(projectId, stage, segmentId);

    public Task EditSegmentAsync(Guid projectId, string segmentId, string title, string narration, CancellationToken cancellationToken = default) =>
        script.EditAsync(projectId, segmentId, title, narration, cancellationToken);

    public Task SelectSegmentVersionAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken = default) =>
        segments.SelectAsync(projectId, stage, segmentId, version, cancellationToken);

    public Task<VoiceView> GetVoiceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        voice.GetAsync(projectId, cancellationToken);

    internal static string SecretName(SecretKey key) => key switch
    {
        SecretKey.LmStudioApiToken => "lm-studio-api-token",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
