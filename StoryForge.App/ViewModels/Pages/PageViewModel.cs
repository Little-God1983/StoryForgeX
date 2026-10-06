using CommunityToolkit.Mvvm.ComponentModel;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>One screen of the app, shown in the content area right of the navigation.</summary>
/// <param name="breadcrumb">The trail shown in the top bar, e.g. "Settings / Profiles".</param>
/// <param name="placeholder">What the screen says until its issue builds it.</param>
public abstract class PageViewModel(string title, string breadcrumb, string placeholder) : ObservableObject
{
    public string Title { get; } = title;

    public string Breadcrumb { get; } = breadcrumb;

    public string Placeholder { get; } = placeholder;
}

public sealed class NewProjectPageViewModel()
    : PageViewModel("New project", "Projects / New project", "Brief, providers, output and run plan come with issue #4.");

public sealed class ResultMatrixPageViewModel()
    : PageViewModel("Result matrix", "Projects / Result matrix", "No project open. Start one from New project.");

public sealed class ProfilesPageViewModel()
    : PageViewModel("Profiles", "Settings / Profiles", "Profiles for every stage come with issue #3.");

public sealed class SettingsPageViewModel()
    : PageViewModel("Settings", "Settings / Providers", "Provider setup comes with issue #2.");
