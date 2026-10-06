using StoryForge.Client;

namespace StoryForge.App.Tests;

internal sealed class FakeStoryForgeClient : IStoryForgeClient
{
    public List<ProviderStatus> Providers { get; } = [];

    public List<ProjectSummary> RecentProjects { get; } = [];

    public Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderStatus>>(Providers);

    public Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectSummary>>(RecentProjects);
}
