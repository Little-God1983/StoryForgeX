using StoryForge.App.ViewModels.Pages;

namespace StoryForge.App.ViewModels;

/// <param name="iconData">Path data for the 24×24 stroke icon left of the title.</param>
public sealed class NavItemViewModel(string title, string iconData, PageViewModel page)
{
    public string Title { get; } = title;

    public string IconData { get; } = iconData;

    public PageViewModel Page { get; } = page;
}
