namespace StoryForge.Client;

/// <summary>
/// Everything the app can ask of the engine. The UI talks to the engine only through this
/// interface: in-process today, over HTTP + SignalR once the engine can run on a server.
/// </summary>
public interface IStoryForgeClient
{
    /// <summary>The providers shown as status pills in the top bar, in display order.</summary>
    Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>The projects listed under "Recent projects", most recent first.</summary>
    Task<IReadOnlyList<ProjectSummary>> GetRecentProjectsAsync(CancellationToken cancellationToken = default);
}
