using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.Pages;

namespace StoryForge.App.Tests;

public sealed class NavigationTests
{
    private static readonly FakeStoryForgeClient Client = new();
    private readonly MainViewModel _main = new(Client, new ProviderStatusBoard(Client), TimeSpan.Zero);

    [Fact]
    public void The_navigation_lists_the_four_screens_in_order()
    {
        Assert.Equal(
            ["New project", "Result matrix", "Profiles", "Settings"],
            _main.NavItems.Select(item => item.Title));
    }

    [Fact]
    public void The_app_opens_on_new_project()
    {
        Assert.Same(_main.NavItems[0], _main.SelectedNavItem);
        Assert.IsType<NewProjectPageViewModel>(_main.CurrentPage);
        Assert.Equal("Projects / New project", _main.Breadcrumb);
    }

    [Theory]
    [InlineData("Result matrix", typeof(ResultMatrixPageViewModel), "Projects / Result matrix")]
    [InlineData("Profiles", typeof(ProfilesPageViewModel), "Settings / Profiles")]
    [InlineData("Settings", typeof(SettingsPageViewModel), "Settings / Providers")]
    public void Selecting_an_item_shows_its_screen_and_breadcrumb(string title, Type pageType, string breadcrumb)
    {
        _main.SelectedNavItem = _main.NavItems.Single(item => item.Title == title);

        Assert.IsType(pageType, _main.CurrentPage);
        Assert.Equal(breadcrumb, _main.Breadcrumb);
    }

    [Fact]
    public void Selecting_an_item_raises_change_notifications_for_the_screen_and_breadcrumb()
    {
        var changed = new List<string?>();
        _main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _main.SelectedNavItem = _main.NavItems[2];

        Assert.Contains(nameof(MainViewModel.CurrentPage), changed);
        Assert.Contains(nameof(MainViewModel.Breadcrumb), changed);
    }

    [Fact]
    public void Clearing_the_selection_keeps_the_current_screen()
    {
        _main.SelectedNavItem = _main.NavItems[2];

        _main.SelectedNavItem = null;

        Assert.IsType<ProfilesPageViewModel>(_main.CurrentPage);
    }
}
