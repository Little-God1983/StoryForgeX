using StoryForge.App.ViewModels;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class ProviderStatusBoardTests
{
    [Fact]
    public async Task A_refresh_publishes_the_latest_statuses()
    {
        var client = new FakeStoryForgeClient();
        client.Providers.Add(new(ProviderId.Ffmpeg, "FFmpeg", ProviderState.Ok, "ffmpeg version 6.1.1"));
        var board = new ProviderStatusBoard(client);
        var raised = 0;
        board.StatusesChanged += (_, _) => raised++;

        await board.RefreshAsync();

        Assert.Equal(ProviderState.Ok, board.Get(ProviderId.Ffmpeg)?.State);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_provider_never_checked_has_no_status()
    {
        var board = new ProviderStatusBoard(new FakeStoryForgeClient());

        Assert.Null(board.Get(ProviderId.ComfyUi));
    }

    [Fact]
    public async Task Refreshes_requested_while_one_runs_add_exactly_one_more_run_after_it()
    {
        // A save during a running check must see a check that started after it, but the
        // checks themselves never overlap.
        var gate = new TaskCompletionSource();
        var client = new GatedClient(gate.Task);
        var board = new ProviderStatusBoard(client);

        var first = board.RefreshAsync();
        var second = board.RefreshAsync();
        var third = board.RefreshAsync();
        gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(2, client.Checks);
        Assert.Equal(1, client.MostAtOnce);
    }

    private sealed class GatedClient(Task gate) : IStoryForgeClient
    {
        private readonly FakeStoryForgeClient _inner = new();
        private int _running;

        public int Checks { get; private set; }

        public int MostAtOnce { get; private set; }

        public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            MostAtOnce = Math.Max(MostAtOnce, ++_running);
            await gate;
            _running--;
            return [];
        }

        public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) => _inner.GetRecentProjectsAsync(cancellationToken);
        public Task<EngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => _inner.GetSettingsAsync(cancellationToken);
        public Task SaveSettingsAsync(EngineSettings settings, CancellationToken cancellationToken = default) => _inner.SaveSettingsAsync(settings, cancellationToken);
        public Task<string> GetEffectiveProjectsFolderAsync(CancellationToken cancellationToken = default) => _inner.GetEffectiveProjectsFolderAsync(cancellationToken);
        public Task<bool> HasSecretAsync(SecretKey key, CancellationToken cancellationToken = default) => _inner.HasSecretAsync(key, cancellationToken);
        public Task SetSecretAsync(SecretKey key, string? value, CancellationToken cancellationToken = default) => _inner.SetSecretAsync(key, value, cancellationToken);
        public Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default) => _inner.GetProfilesAsync(cancellationToken);
        public Task<ProfileSummary> CreateProfileAsync(ProfileKind kind, string name, CancellationToken cancellationToken = default) => _inner.CreateProfileAsync(kind, name, cancellationToken);
        public Task<ProfileVersion> GetProfileVersionAsync(Guid profileId, int? version = null, CancellationToken cancellationToken = default) => _inner.GetProfileVersionAsync(profileId, version, cancellationToken);
        public Task<ProfileVersion> SaveProfileVersionAsync(Guid profileId, ProfileContent content, CancellationToken cancellationToken = default) => _inner.SaveProfileVersionAsync(profileId, content, cancellationToken);
        public Task<string> ImportReferenceFileAsync(string sourcePath, CancellationToken cancellationToken = default) => _inner.ImportReferenceFileAsync(sourcePath, cancellationToken);
        public Task<IReadOnlyList<string>> GetWorkflowTemplatesAsync(CancellationToken cancellationToken = default) => _inner.GetWorkflowTemplatesAsync(cancellationToken);
        public Task<Project> CreateProjectAsync(ProjectSetup setup, CancellationToken cancellationToken = default) => _inner.CreateProjectAsync(setup, cancellationToken);
        public Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => _inner.GetProjectAsync(projectId, cancellationToken);
        public event EventHandler<StageUpdate>? StageUpdated
        {
            add => _inner.StageUpdated += value;
            remove => _inner.StageUpdated -= value;
        }
        public Task StartRunAsync(Guid projectId, CancellationToken cancellationToken = default) => _inner.StartRunAsync(projectId, cancellationToken);
        public Task RegenerateAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default) => _inner.RegenerateAsync(projectId, stage, cancellationToken);
        public Task CancelAsync(Guid projectId, PipelineStage stage, CancellationToken cancellationToken = default) => _inner.CancelAsync(projectId, stage, cancellationToken);
        public Task ApproveAsync(Guid projectId, PipelineStage stage, int version, CancellationToken cancellationToken = default) => _inner.ApproveAsync(projectId, stage, version, cancellationToken);
        public Task<FactSheetView> GetFactSheetAsync(Guid projectId, int? version = null, CancellationToken cancellationToken = default) => _inner.GetFactSheetAsync(projectId, version, cancellationToken);
        public Task<FactSheetView> ChangeFactAsync(Guid projectId, int version, string factId, FactChange change, CancellationToken cancellationToken = default) => _inner.ChangeFactAsync(projectId, version, factId, change, cancellationToken);
        public Task<ScriptView> GetScriptAsync(Guid projectId, CancellationToken cancellationToken = default) => _inner.GetScriptAsync(projectId, cancellationToken);
        public Task ApproveSegmentAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken = default) => _inner.ApproveSegmentAsync(projectId, segmentId, version, cancellationToken);
        public Task ApproveScriptAsync(Guid projectId, CancellationToken cancellationToken = default) => _inner.ApproveScriptAsync(projectId, cancellationToken);
        public Task RegenerateSegmentAsync(Guid projectId, string segmentId, CancellationToken cancellationToken = default) => _inner.RegenerateSegmentAsync(projectId, segmentId, cancellationToken);
        public Task EditSegmentAsync(Guid projectId, string segmentId, string title, string narration, CancellationToken cancellationToken = default) => _inner.EditSegmentAsync(projectId, segmentId, title, narration, cancellationToken);
        public Task SelectSegmentVersionAsync(Guid projectId, string segmentId, int version, CancellationToken cancellationToken = default) => _inner.SelectSegmentVersionAsync(projectId, segmentId, version, cancellationToken);
    }
}
