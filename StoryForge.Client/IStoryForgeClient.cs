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
    /// <param name="note">
    /// Script only: why it is written again, e.g. "too long". The model gets it, and it is kept with the
    /// new version. Null or blank for none.
    /// </param>
    /// <exception cref="KeyNotFoundException">No such project or segment.</exception>
    /// <exception cref="InvalidOperationException">The whole stage is running, or a note came for a segment already being written.</exception>
    /// <exception cref="ArgumentException">A note for a stage other than Script.</exception>
    Task RegenerateSegmentAsync(Guid projectId, PipelineStage stage, string segmentId, string? note = null, CancellationToken cancellationToken = default);

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

    /// <summary>The Storyboard stage: its state, its shots with their versions, and the words they were cut from.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<StoryboardView> GetStoryboardAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Your change to a shot's description, who and what is in it, or "continues the shot before": its next version, to approve.</summary>
    /// <exception cref="KeyNotFoundException">No such project or shot.</exception>
    /// <exception cref="ArgumentException">The description is empty, or more than <see cref="Shot.MaxCast"/> are in it.</exception>
    /// <exception cref="InvalidOperationException">The shot or the storyboard is running.</exception>
    Task EditShotAsync(Guid projectId, string shotKey, ShotChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves where a shot starts or ends by <paramref name="words"/> (1 later, -1 earlier). The shot
    /// next to it grows or shrinks, so the segment still adds up to its audio; both get a new version.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project or shot.</exception>
    /// <exception cref="ArgumentException">The cut is at the segment's edge, or a shot would break a length limit.</exception>
    /// <exception cref="InvalidOperationException">A shot or the storyboard is running.</exception>
    Task MoveCutAsync(Guid projectId, string shotKey, ShotEdge edge, int words, CancellationToken cancellationToken = default);

    /// <summary>Splits a shot in two: the new shot starts at word <paramref name="atWord"/> of the segment.</summary>
    /// <exception cref="KeyNotFoundException">No such project or shot.</exception>
    /// <exception cref="ArgumentException">The word is not inside the shot, or a half would break a length limit.</exception>
    /// <exception cref="InvalidOperationException">The shot or the storyboard is running.</exception>
    Task SplitShotAsync(Guid projectId, string shotKey, int atWord, CancellationToken cancellationToken = default);

    /// <summary>Merges a shot with the next shot of its segment.</summary>
    /// <exception cref="KeyNotFoundException">No such project or shot.</exception>
    /// <exception cref="ArgumentException">It is the segment's last shot, or the merged shot would be longer than a clip.</exception>
    /// <exception cref="InvalidOperationException">A shot or the storyboard is running.</exception>
    Task MergeShotAsync(Guid projectId, string shotKey, CancellationToken cancellationToken = default);

    /// <summary>The References stage: the list, the images with their versions, the pool, the rest of the cast.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<ReferencesView> GetReferencesAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Changes the reference list or the pool: add, remove, merge, the look, which pictures a reference is made from.</summary>
    /// <exception cref="KeyNotFoundException">No such project, reference or picture.</exception>
    /// <exception cref="ArgumentException">The change is missing what its kind needs, or names a reference twice.</exception>
    /// <exception cref="InvalidOperationException">The References stage is running.</exception>
    Task ChangeReferencesAsync(Guid projectId, ReferenceChange change, CancellationToken cancellationToken = default);

    /// <summary>Your own picture, copied into the pool and used first for the reference, like a found one; once the images exist, they are made again from it.</summary>
    /// <exception cref="KeyNotFoundException">No such project or reference.</exception>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ArgumentException">The file is not a PNG, JPEG or WebP picture, or is under <see cref="PoolImage.MinUsableSide"/> px on a side.</exception>
    /// <exception cref="InvalidOperationException">The References stage is running.</exception>
    Task AddOwnPictureAsync(Guid projectId, string referenceId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The list is confirmed: its images are made (an empty list waits to be approved as it is).
    /// Past the list, only what is missing is made: references with no image, and images never made.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project, or no list yet.</exception>
    /// <exception cref="InvalidOperationException">The References stage is running.</exception>
    Task MakeReferenceImagesAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The timeline: puts the reference in the shot, or takes it out, by changing who and what is in
    /// the shot, as the Board panel does. The shot keeps its approval.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project, reference or shot.</exception>
    /// <exception cref="ArgumentException">The shot has <see cref="Shot.MaxCast"/> in it already.</exception>
    /// <exception cref="InvalidOperationException">The storyboard is running.</exception>
    Task SetReferenceInShotAsync(Guid projectId, string referenceId, string shotKey, bool inShot, CancellationToken cancellationToken = default);

    /// <summary>"+ image": one more image of the reference, made from its pictures.</summary>
    Task AddReferenceImageAsync(Guid projectId, string referenceId, CancellationToken cancellationToken = default);

    /// <summary>Searches the project's sources for the reference again, then makes its images from what was found.</summary>
    Task SearchSourcesAgainAsync(Guid projectId, string referenceId, CancellationToken cancellationToken = default);

    /// <summary>Makes the reference's images from its look alone: the one case where text alone is allowed.</summary>
    Task MakeFromDescriptionAsync(Guid projectId, string referenceId, CancellationToken cancellationToken = default);

    /// <summary>The Stills stage: its state, and each shot's still with its versions and whether it waits in the batch.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<StillsView> GetStillsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Regenerate on a still (or Edit prompt…, with your prompt): it waits in the batch, and Continue
    /// pipeline makes it again with the others, so the model loads once for them all.
    /// </summary>
    /// <param name="prompt">Your prompt, used as it is; null or blank for one Claude writes.</param>
    /// <exception cref="KeyNotFoundException">No such project, or the shot has no still yet.</exception>
    /// <exception cref="InvalidOperationException">The still or the Stills stage is being made.</exception>
    Task QueueStillAsync(Guid projectId, string shotKey, string? prompt = null, CancellationToken cancellationToken = default);

    /// <summary>Takes a still out of the batch: it stays as it is.</summary>
    /// <exception cref="KeyNotFoundException">No such project, or the shot has no still yet.</exception>
    /// <exception cref="InvalidOperationException">The still or the Stills stage is being made.</exception>
    Task UnqueueStillAsync(Guid projectId, string shotKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Continue pipeline: the stills waiting in the batch are made again, one after another. When the
    /// stills were never made (references approved before), they are all made.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    /// <exception cref="InvalidOperationException">Nothing is waiting, the references are not approved, or the stills are being made.</exception>
    Task ContinuePipelineAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The Clips stage: its state, and each shot's clip with its versions and whether it waits to be made again.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task<ClipsView> GetClipsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Regenerate on a clip (or Edit prompt…, with your prompt; or Make with…, with another video
    /// profile): it waits, and Make clips makes it again with the others, each model's together.
    /// </summary>
    /// <param name="prompt">Your prompt, used as it is; null or blank for one Claude writes.</param>
    /// <param name="profile">Another video profile to make it with (the fallback); null for the project's. The app never switches on its own.</param>
    /// <exception cref="KeyNotFoundException">No such project, or the shot has no clip cell yet.</exception>
    /// <exception cref="InvalidOperationException">The clip or the Clips stage is being made.</exception>
    Task QueueClipAsync(Guid projectId, string shotKey, string? prompt = null, ProfileRef? profile = null, CancellationToken cancellationToken = default);

    /// <summary>Takes a clip out of the waiting ones: it stays as it is.</summary>
    /// <exception cref="KeyNotFoundException">No such project, or the shot has no clip cell yet.</exception>
    /// <exception cref="InvalidOperationException">The clip or the Clips stage is being made.</exception>
    Task UnqueueClipAsync(Guid projectId, string shotKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Make clips: the clips waiting are made again, and the shots with none yet get theirs, in one
    /// run. When the clips were never made (stills approved before), they are all made.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    /// <exception cref="InvalidOperationException">Nothing is waiting, the stills are not approved, or the clips are being made.</exception>
    Task MakeClipsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What Rerun pipeline from here on this cell would make again, as the question before it says:
    /// "This remakes S04's voice, its 4 shots and their 4 stills. Other segments stay as they are."
    /// </summary>
    /// <param name="key">The segment ("S04"), the shot or still (its shot's key), or the reference image ("R01/1").</param>
    /// <exception cref="KeyNotFoundException">No such project, or nothing is made for the cell yet.</exception>
    Task<RerunPlan> GetRerunPlanAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rerun pipeline from here: the cell is made again (your own work stays), then, stage by stage,
    /// what is made from it and nothing else; each waits at its gate as a run does. From a reference
    /// image, the stills that use it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such project, or nothing is made for the cell yet.</exception>
    /// <exception cref="InvalidOperationException">The cell is being made, or nothing would be made from it.</exception>
    Task RerunFromHereAsync(Guid projectId, PipelineStage stage, string key, CancellationToken cancellationToken = default);

    /// <summary>Stops the project's reruns: what waits for approval stays as it is, and nothing more is made after it.</summary>
    /// <exception cref="KeyNotFoundException">No such project.</exception>
    Task StopRerunAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Your own picture as the still's next version (Replace with file…), to review like any other; nothing is made.</summary>
    /// <exception cref="KeyNotFoundException">No such project or shot.</exception>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ArgumentException">The file is not a PNG, JPEG or WebP picture.</exception>
    /// <exception cref="InvalidOperationException">The still or the Stills stage is being made.</exception>
    Task ReplaceStillAsync(Guid projectId, string shotKey, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Test render on the Profiles screen: one picture from an image profile (as it is in the editor,
    /// saved or not) with <paramref name="line"/> in its prompt template. It goes into no project.
    /// </summary>
    /// <exception cref="ArgumentException">The line is empty.</exception>
    /// <exception cref="InvalidOperationException">The profile cannot make a picture, or ComfyUI could not.</exception>
    Task<TestRender> TestRenderAsync(ProfileContent profile, string line, CancellationToken cancellationToken = default);
}
