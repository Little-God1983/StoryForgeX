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
    }
}
