using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Settings;

/// <summary>A value and the words a dropdown shows for it.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    // What screen readers announce for a dropdown item.
    public override string ToString() => Label;
}

/// <summary>
/// One provider card on the Settings screen: its fields (validated) and its live status. Any edit
/// of a field raises <see cref="Edited"/>, which the page turns into a debounced save.
/// </summary>
public abstract partial class ProviderCardViewModel(ProviderId id, string title, string kind) : ObservableValidator
{
    private static readonly HashSet<string> StatusProperties =
        [nameof(State), nameof(StateText), nameof(Detail), nameof(Checked), nameof(HasErrors)];

    private bool _loading;

    public ProviderId Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>What kind of provider this is, e.g. "LLM · CLI process".</summary>
    public string Kind { get; } = kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private ProviderState _state;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _checked;

    public string StateText => Checked ? ProviderStateText.For(State) : "checking…";

    public event EventHandler? Edited;

    /// <param name="checkedYet">False before the first check came back: the card says "checking…".</param>
    public void UpdateStatus(ProviderStatus? status, bool checkedYet)
    {
        State = status?.State ?? ProviderState.NotSetUp;
        Detail = status?.Detail;
        Checked = checkedYet;
    }

    /// <summary>Fills the fields from saved settings without counting it as an edit.</summary>
    public void Load(EngineSettings settings)
    {
        _loading = true;
        try
        {
            LoadFields(settings);
            ValidateAllProperties();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The settings with this card's fields applied.</summary>
    public abstract EngineSettings ApplyTo(EngineSettings settings);

    protected abstract void LoadFields(EngineSettings settings);

    /// <summary>Properties that change without the user editing a saved field.</summary>
    protected virtual bool IsNotAField(string propertyName) => StatusProperties.Contains(propertyName);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not null && !IsNotAField(e.PropertyName))
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }
}
