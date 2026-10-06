using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels;

/// <summary>
/// One status pill in the top bar, e.g. "ComfyUI · ok"; the reason shows as its tooltip. Pills
/// exist from the start ("checking…") and are updated in place, so they never flicker.
/// </summary>
public sealed partial class ProviderPillViewModel(ProviderId id, string name) : ObservableObject
{
    public ProviderId Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private string _name = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private ProviderState _state;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _checked;

    public string Text => $"{Name} · {(Checked ? ProviderStateText.For(State) : "checking…")}";

    /// <param name="checkedYet">False before the first check came back.</param>
    public void Update(ProviderStatus? status, bool checkedYet)
    {
        Name = status?.Name ?? Name;
        State = status?.State ?? ProviderState.NotSetUp;
        Detail = status?.Detail;
        Checked = checkedYet;
    }
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
