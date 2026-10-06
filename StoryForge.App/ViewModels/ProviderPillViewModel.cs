using StoryForge.Client;

namespace StoryForge.App.ViewModels;

/// <summary>One status pill in the top bar, e.g. "ComfyUI · ok"; the reason shows as its tooltip.</summary>
public sealed class ProviderPillViewModel(ProviderStatus status)
{
    public ProviderId Id => status.Id;

    public string Name => status.Name;

    public ProviderState State => status.State;

    public string? Detail => status.Detail;

    public string Text => $"{Name} · {ProviderStateText.For(State)}";
}

public static class ProviderStateText
{
    public static string For(ProviderState state) => state switch
    {
        ProviderState.Ok => "ok",
        ProviderState.Off => "off",
        ProviderState.Error => "error",
        _ => "not set up",
    };
}
