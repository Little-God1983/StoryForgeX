using CommunityToolkit.Mvvm.ComponentModel;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>One screen of the app, shown in the content area right of the navigation.</summary>
/// <param name="breadcrumb">The trail shown in the top bar, e.g. "Settings / Profiles".</param>
/// <param name="placeholder">What the screen says until its issue builds it.</param>
public abstract class PageViewModel(string title, string breadcrumb, string placeholder) : ObservableObject
{
    private string _breadcrumb = breadcrumb;

    public string Title { get; } = title;

    public string Breadcrumb
    {
        get => _breadcrumb;
        protected set => SetProperty(ref _breadcrumb, value);
    }

    public string Placeholder { get; } = placeholder;
}
