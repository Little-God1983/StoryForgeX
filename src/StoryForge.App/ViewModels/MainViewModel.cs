using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStoryForgeClient _client;

    public MainViewModel(IStoryForgeClient client)
    {
        _client = client;
        NavItems =
        [
            new("New project", "M12 5v14M5 12h14", new NewProjectPageViewModel()),
            new("Result matrix", "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z", new ResultMatrixPageViewModel()),
            new("Profiles", "M4 6h16M4 12h10M4 18h16", new ProfilesPageViewModel()),
            new("Settings", "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1", new SettingsPageViewModel()),
        ];
        _selectedNavItem = NavItems[0];
        _currentPage = NavItems[0].Page;
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel? _selectedNavItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    private PageViewModel _currentPage;

    public string Breadcrumb => CurrentPage.Breadcrumb;

    public ObservableCollection<ProviderPillViewModel> ProviderPills { get; } = [];

    public ObservableCollection<ProjectSummary> RecentProjects { get; } = [];

    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>Fills the status pills and the recent projects from the engine.</summary>
    public async Task LoadAsync()
    {
        var providers = await _client.GetProviderStatusesAsync();
        var projects = await _client.GetRecentProjectsAsync();

        ProviderPills.Clear();
        foreach (var provider in providers)
        {
            ProviderPills.Add(new ProviderPillViewModel(provider));
        }

        RecentProjects.Clear();
        foreach (var project in projects)
        {
            RecentProjects.Add(project);
        }
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        // The list clears its selection when its items are rebuilt; keep the screen in that case.
        if (value is not null)
        {
            CurrentPage = value.Page;
        }
    }
}
