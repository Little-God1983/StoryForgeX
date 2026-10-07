using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Profiles;
using StoryForge.App.ViewModels.Settings;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.NewProject;

/// <summary>One profile picker on a provider card, e.g. "Script profile": the profiles of one kind, latest versions.</summary>
public sealed partial class ProfileSlotViewModel(ProfileKind kind, string label) : ObservableObject
{
    public ProfileKind Kind { get; } = kind;

    public string Label { get; } = label;

    public ObservableCollection<ProfileSummary> Choices { get; } = [];

    private ProfileSummary? _selected;
    private bool _refilling;

    /// <summary>
    /// The picked profile. Writes while the choices are refilled are ignored: WPF's dropdown writes
    /// null when its list is cleared, which would drop the pick and everything that came with it.
    /// </summary>
    public ProfileSummary? Selected
    {
        get => _selected;
        set
        {
            if (!_refilling)
            {
                SetProperty(ref _selected, value);
            }
        }
    }

    public bool HasChoices => Choices.Count > 0;

    public string MissingText => $"No {ProfileKinds.Label(Kind).Replace(" (LLM)", "").ToLowerInvariant()} profile yet. Create one under Profiles.";

    /// <summary>Replaces the choices; the picked profile stays picked (at its newest version) if it is still there.</summary>
    public void SetChoices(IEnumerable<ProfileSummary> profiles)
    {
        var keep = Selected?.Id;
        _refilling = true;
        try
        {
            Choices.Clear();
            foreach (var profile in profiles.Where(p => p.Kind == Kind))
            {
                Choices.Add(profile);
            }
        }
        finally
        {
            _refilling = false;
        }
        var pick = Choices.FirstOrDefault(p => p.Id == keep) ?? Choices.FirstOrDefault();
        if (!SetProperty(ref _selected, pick, nameof(Selected)))
        {
            // Unchanged, but announced again: the dropdown lost its selection with the old list.
            OnPropertyChanged(nameof(Selected));
        }
        OnPropertyChanged(nameof(HasChoices));
    }

    /// <summary>Picks the profile with one of <paramref name="names"/>; false (and no change) when there is none.</summary>
    public bool SelectByName(IEnumerable<string> names)
    {
        var match = names.Select(n => Choices.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(p => p is not null);
        if (match is null)
        {
            return false;
        }
        Selected = match;
        return true;
    }

    public ProfileRef? ToRef() => Selected is { } p ? new ProfileRef(p.Id, p.LatestVersion) : null;
}

/// <summary>
/// One provider card on the New project screen ("Research &amp; script", "Voice", "Still images",
/// "Video clips"): provider, model and the profile per stage it covers.
/// </summary>
public sealed partial class StageCardViewModel : ObservableObject
{
    public StageCardViewModel(
        string title, int order, IReadOnlyList<Choice<string>> providers, IReadOnlyList<Choice<string>> models, params ProfileSlotViewModel[] slots)
    {
        Title = title;
        Order = order;
        ProviderChoices = providers;
        ModelChoices = models;
        Slots = slots;
        _provider = providers[0].Value;
        _model = models[0].Value;
    }

    public string Title { get; }

    /// <summary>Position on the screen, left to right; "later stages" are the cards after it.</summary>
    public int Order { get; }

    public IReadOnlyList<Choice<string>> ProviderChoices { get; }

    public IReadOnlyList<Choice<string>> ModelChoices { get; }

    public IReadOnlyList<ProfileSlotViewModel> Slots { get; }

    [ObservableProperty]
    private string _provider;

    [ObservableProperty]
    private string _model;

    /// <summary>
    /// Takes over what <paramref name="source"/> has where this card can use it: the provider and
    /// model if they are among this card's choices, and for each slot a profile with the same name
    /// as one picked on the source. Anything without a match keeps its choice.
    /// </summary>
    public void ApplyFrom(StageCardViewModel source)
    {
        if (ProviderChoices.Any(c => c.Value == source.Provider))
        {
            Provider = source.Provider;
        }
        if (ModelChoices.Any(c => c.Value == source.Model))
        {
            Model = source.Model;
        }
        var names = source.Slots.Select(s => s.Selected?.Name).OfType<string>().ToList();
        foreach (var slot in Slots)
        {
            slot.SelectByName(names);
        }
    }
}
