using StoryForge.Client;

namespace StoryForge.Engine;

/// <summary>The client the desktop app uses while engine and UI share one process.</summary>
internal sealed class InProcessStoryForgeClient : IStoryForgeClient
{
    private static readonly IReadOnlyList<ProviderStatus> Providers =
    [
        new("Claude CLI", ProviderState.NotSetUp),
        new("ComfyUI", ProviderState.NotSetUp),
        new("CAX", ProviderState.NotSetUp),
        new("Resolve", ProviderState.NotSetUp),
    ];

    public Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Providers);

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectSummary>>([]);
}
