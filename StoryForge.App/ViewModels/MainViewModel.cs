using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The providers with a pill in the top bar, as on the design canvas.</summary>
    private static readonly ProviderId[] TopBarProviders =
        [ProviderId.ClaudeCli, ProviderId.ComfyUi, ProviderId.ContentAutomatorX, ProviderId.DavinciResolve];

    private readonly IStoryForgeClient _client;
    private readonly ProviderStatusBoard _board;
    private readonly SettingsPageViewModel _settings;

    /// <param name="saveDelay">How long Settings waits after the last edit before saving.</param>
    public MainViewModel(IStoryForgeClient client, ProviderStatusBoard board, TimeSpan saveDelay)
    {
        _client = client;
        _board = board;
        _settings = new SettingsPageViewModel(client, board, saveDelay);
        NavItems =
        [
            new("New project", "M12 5v14M5 12h14", new NewProjectPageViewModel()),
            new("Result matrix", "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z", new ResultMatrixPageViewModel()),
            new("Profiles", "M4 6h16M4 12h10M4 18h16", new ProfilesPageViewModel()),
            new("Settings", "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1", _settings),
        ];
        _selectedNavItem = NavItems[0];
        _currentPage = NavItems[0].Page;
        _currentPage.PropertyChanged += OnPagePropertyChanged;
        board.StatusesChanged += (_, _) => ShowPills();
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

    /// <summary>
    /// Loads the settings and the recent projects, then checks the providers. The checks come last
    /// because they take seconds (a CLI starting, a port timing out); the screens are filled first.
    /// </summary>
    public async Task LoadAsync()
    {
        await _settings.LoadAsync();

        var projects = await _client.GetRecentProjectsAsync();
        RecentProjects.Clear();
        foreach (var project in projects)
        {
            RecentProjects.Add(project);
        }
        OnPropertyChanged(nameof(HasRecentProjects));

        await _board.RefreshAsync();
    }

    /// <summary>Writes anything still waiting to be saved; called when the window closes.</summary>
    public Task FlushAsync() => _settings.FlushAsync();

    public bool HasPendingSave => _settings.HasPendingSave;

    private void ShowPills()
    {
        ProviderPills.Clear();
        foreach (var id in TopBarProviders)
        {
            if (_board.Get(id) is { } status)
            {
                ProviderPills.Add(new ProviderPillViewModel(status));
            }
        }
    }

    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        // Null comes from Ctrl+click on the selected item; the screen stays and the window puts
        // the highlight back (MainWindow.Navigation_SelectionChanged).
        if (value is not null)
        {
            CurrentPage = value.Page;
        }
    }

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnPagePropertyChanged;
        }
        newValue.PropertyChanged += OnPagePropertyChanged;
    }

    // A page can change its own breadcrumb (Settings: "Settings / Paths & cache").
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.Breadcrumb))
        {
            OnPropertyChanged(nameof(Breadcrumb));
        }
    }
}
