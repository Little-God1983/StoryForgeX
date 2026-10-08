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

    /// <summary>Saves a new project with every choice; nothing runs yet.</summary>
    /// <exception cref="ArgumentException">A required choice is missing or out of range, or a profile version does not exist.</exception>
    Task<Project> CreateProjectAsync(ProjectSetup setup, CancellationToken cancellationToken = default);

    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A stage changed state or reported activity, for any project. Raised on an engine thread;
    /// the UI moves it to its own.
    /// </summary>
    event EventHandler<StageUpdate>? StageUpdated;

    /// <summary>
    /// Runs the project from its first stage that is not approved yet. Returns once the run is
    /// queued; <see cref="StageUpdated"/> reports how it goes.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task StartRunAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Runs a stage again: Regenerate after a result, Retry after a failure. The earlier versions stay.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default);

    /// <summary>Stops a stage that is running or waiting to run; it goes back to where it stood before.</summary>
    Task CancelAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default);

    /// <summary>Approves one version of a stage's result, and the run goes on with the next stage.</summary>
    /// <exception cref="KeyNotFoundException">No such project or version.</exception>
    /// <exception cref="InvalidOperationException">The stage is running.</exception>
    Task ApproveAsync(Guid projectId, PipelineStage stage, int version, CancellationToken cancellationToken = default);

    /// <summary>The fact sheet screen's content; <paramref name="version"/> null means the current version.</summary>
    /// <exception cref="KeyNotFoundException">No such project or version.</exception>
    Task<FactSheetView> GetFactSheetAsync(Guid projectId, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes one fact of <paramref name="version"/>: its wording, weight, or whether it is left out.
    /// Your changes collect in one edited version until you approve it; a generated or approved
    /// version is never changed, so the first change after one makes a new version.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project, version or fact.</exception>
    /// <exception cref="ArgumentException">The wording is empty or the weight is outside 1 to 10.</exception>
    /// <exception cref="InvalidOperationException">The stage is running.</exception>
    Task<FactSheetView> ChangeFactAsync(Guid projectId, int version, string factId, FactChange change, CancellationToken cancellationToken = default);

    /// <summary>The Script stage: its state, its segments with their versions, and the facts they use.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<ScriptView> GetScriptAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves one version of a segment of <paramref name="stage"/> (Script or Voice). When every
    /// segment is approved, the run goes on.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project, segment or version.</exception>
    /// <exception cref="InvalidOperationException">The segment or its stage is running, or the stage has no segments.</exception>
    Task ApproveSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken = default);

    /// <summary>"Approve remaining": approves every segment of the stage as it stands, and the run goes on.</summary>
    /// <exception cref="KeyNotFoundException">No such project, or the stage has no segments yet.</exception>
    /// <exception cref="InvalidOperationException">The stage or a segment is running.</exception>
    Task ApproveSegmentsAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default);

    /// <summary>Makes one segment of the stage again (writes or speaks it), keeping the rest. The earlier versions stay.</summary>
    /// <exception cref="KeyNotFoundException">No such project or segment.</exception>
    /// <exception cref="InvalidOperationException">The whole stage is running.</exception>
    Task RegenerateSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, CancellationToken cancellationToken = default);

    /// <summary>Stops making one segment again; it goes back to the version it had.</summary>
    Task CancelSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, CancellationToken cancellationToken = default);

    /// <summary>Your own wording of a segment, saved as its next version, to review and approve like any other.</summary>
    /// <exception cref="KeyNotFoundException">No such project or segment.</exception>
    /// <exception cref="ArgumentException">The title or the narration is empty.</exception>
    /// <exception cref="InvalidOperationException">The segment or the script is being written.</exception>
    Task EditSegmentAsync(Guid projectId, string segmentId, string title, string narration, CancellationToken cancellationToken = default);

    /// <summary>Switches a segment of the stage back (or forward) to one of its versions, which then needs approving.</summary>
    /// <exception cref="KeyNotFoundException">No such project, segment or version.</exception>
    /// <exception cref="InvalidOperationException">The segment or its stage is running.</exception>
    Task SelectSegmentVersionAsync(Guid projectId, PipelineStage stage, string segmentId, int version, CancellationToken cancellationToken = default);

    /// <summary>The Voice stage: its state, each segment's audio with its words and versions, and the voice total.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<VoiceView> GetVoiceAsync(Guid projectId, CancellationToken cancellationToken = default);
}
