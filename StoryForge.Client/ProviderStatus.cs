namespace StoryForge.Client;

/// <summary>How far a provider (Claude CLI, ComfyUI, …) is usable right now.</summary>
public enum ProviderState
{
    /// <summary>Nothing has been configured for this provider yet.</summary>
    NotSetUp,
}

/// <param name="Name">The short name shown on the status pill, e.g. "ComfyUI".</param>
public sealed record ProviderStatus(string Name, ProviderState State);
