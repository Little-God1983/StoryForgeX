using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Profiles;

/// <summary>One entry of the profile list: "Painted dark fantasy · v3", grouped under its kind.</summary>
public sealed partial class ProfileListItemViewModel(ProfileSummary summary) : ObservableObject
{
    public Guid Id { get; } = summary.Id;

    public ProfileKind Kind { get; } = summary.Kind;

    public string Name { get; } = summary.Name;

    public string KindLabel => ProfileKinds.Label(Kind);

    /// <summary>The list's group header, upper case like the other section labels.</summary>
    public string GroupTitle => KindLabel.ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _latestVersion = summary.LatestVersion;

    /// <summary>Edits not saved as a version yet; the list marks the entry.</summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public string Label => $"{Name} · v{LatestVersion}";

    // What screen readers announce for the list item.
    public override string ToString() => HasUnsavedChanges ? $"{Label}, unsaved changes" : Label;
}
