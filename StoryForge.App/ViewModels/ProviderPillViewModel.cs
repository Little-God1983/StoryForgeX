using StoryForge.Client;

namespace StoryForge.App.ViewModels;

/// <summary>One status pill in the top bar, e.g. "ComfyUI · not set up".</summary>
public sealed class ProviderPillViewModel(ProviderStatus status)
{
    public string Name => status.Name;

    public ProviderState State => status.State;

    public string Text => $"{Name} · {StateText}";

    private string StateText => State switch
    {
        ProviderState.NotSetUp => "not set up",
        _ => throw new InvalidOperationException($"No pill text for provider state {State}."),
    };
}
