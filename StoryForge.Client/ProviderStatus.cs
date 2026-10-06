namespace StoryForge.Client;

/// <summary>Every provider the app knows, in the order the Settings screen lists them.</summary>
public enum ProviderId
{
    ClaudeCli,
    LmStudio,
    ComfyUi,
    Ffmpeg,
    ContentAutomatorX,
    DavinciResolve,
}

/// <summary>How far a provider (Claude CLI, ComfyUI, …) is usable right now.</summary>
public enum ProviderState
{
    /// <summary>Nothing has been configured for this provider yet.</summary>
    NotSetUp,

    /// <summary>The last check reached the provider and it answered as expected.</summary>
    Ok,

    /// <summary>Configured, but not running or not reachable (e.g. LM Studio's server is stopped).</summary>
    Off,

    /// <summary>Reachable or startable, but something is wrong (bad path, rejected token, …).</summary>
    Error,
}

/// <param name="Name">The short name shown on the status pill, e.g. "ComfyUI".</param>
/// <param name="Detail">The reason or the version behind the state, e.g. "not reachable at 127.0.0.1:8188".</param>
public sealed record ProviderStatus(ProviderId Id, string Name, ProviderState State, string? Detail = null);
