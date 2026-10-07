using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The providers with a pill in the top bar, as on the design canvas.</summary>
    private static readonly (ProviderId Id, string Name)[] TopBarProviders =
    [
        (ProviderId.ClaudeCli, "Claude CLI"),
        (ProviderId.ComfyUi, "ComfyUI"),
        (ProviderId.ContentAutomatorX, "CAX"),
        (ProviderId.DavinciResolve, "Resolve"),
    ];

    private readonly IStoryForgeClient _client;
    private readonly ProviderStatusBoard _board;
    private readonly SettingsPageViewModel _settings;
    private readonly ProfilesPageViewModel _profiles;
    private readonly NewProjectPageViewModel _newProject;
    private readonly ResultMatrixPageViewModel _matrix;

    /// <param name="saveDelay">How long Settings waits after the last edit before saving.</param>
    public MainViewModel(IStoryForgeClient client, ProviderStatusBoard board, TimeSpan saveDelay)
    {
        _client = client;
        _board = board;
        _settings = new SettingsPageViewModel(client, board, saveDelay);
        _profiles = new ProfilesPageViewModel(client);
        _newProject = new NewProjectPageViewModel(client);
        _matrix = new ResultMatrixPageViewModel(client);
        _newProject.ProjectStarted += (_, project) => PageShowing = OnProjectStartedAsync(project);
        NavItems =
        [
            new("New project", "M12 5v14M5 12h14", _newProject),
            new("Result matrix", "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z", _matrix),
            new("Profiles", "M4 6h16M4 12h10M4 18h16", _profiles),
            new("Settings", "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1", _settings),
        ];
        _selectedNavItem = NavItems[0];
        _currentPage = NavItems[0].Page;
        _currentPage.PropertyChanged += OnPagePropertyChanged;
        ProviderPills = [.. TopBarProviders.Select(p => new ProviderPillViewModel(p.Id, p.Name))];
        board.StatusesChanged += (_, _) => ShowPills();
        // "Research: needs review" under Recent projects follows the run. Lines of activity change no state.
        client.StageUpdated += (_, update) =>
        {
            if (update.Activity is null)
            {
                UiThread.Run(() => _ = LoadRecentProjectsAsync());
            }
        };
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel? _selectedNavItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    private PageViewModel _currentPage;

    public string Breadcrumb => CurrentPage.Breadcrumb;

    public IReadOnlyList<ProviderPillViewModel> ProviderPills { get; }

    public ObservableCollection<ProjectSummary> RecentProjects { get; } = [];

    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>Why the recent projects could not be read; shown in their place. Null when all is well.</summary>
    [ObservableProperty]
    private string? _recentProjectsError;

    /// <summary>What opening the current screen started (Profiles reads its lists); tests await it.</summary>
    public Task PageShowing { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Loads the settings, the profiles and the recent projects, then checks the providers. The checks come last
    /// because they take seconds (a CLI starting, a port timing out); the screens are filled first.
    /// </summary>
    public async Task LoadAsync()
    {
        // Each step on its own: settings that cannot be read must not cost the recent projects
        // and the checks. What failed is reported once everything else has loaded.
        Exception? settingsFailure = null;
        try
        {
            await _settings.LoadAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading settings failed: {ex}");
            settingsFailure = ex;
        }
        // Not awaited yet: the workflow templates folder may be on a network share that takes
        // its timeout to answer, and the home screen and the checks should not wait for that.
        var profiles = _profiles.LoadAsync();
        await _newProject.ShowAsync();
        await LoadRecentProjectsAsync();

        try
        {
            await _board.RefreshAsync();
        }
        catch (Exception ex)
        {
            // The app works without statuses; the pills keep saying "checking…" until a later check.
            Debug.WriteLine($"First provider check failed: {ex}");
        }
        await profiles;
        if (settingsFailure is not null)
        {
            throw new InvalidOperationException($"The settings could not be read: {settingsFailure.Message}", settingsFailure);
        }
    }

    /// <summary>Opens a project from Recent projects in the result matrix.</summary>
    [RelayCommand]
    private async Task OpenRecentProjectAsync(ProjectSummary project)
    {
        await _matrix.OpenAsync(project.Id);
        SelectedNavItem = NavItems.Single(item => item.Page == _matrix);
    }

    private async Task OnProjectStartedAsync(Project project)
    {
        SelectedNavItem = NavItems.Single(item => item.Page == _matrix);
        await _matrix.StartRunAsync(project);
        await LoadRecentProjectsAsync();
    }

    /// <summary>
    /// A failure stays in the list's place: the app works without it, and closing the app over a
    /// locked database would be worse than a line of red text.
    /// </summary>
    private async Task LoadRecentProjectsAsync()
    {
        try
        {
            var projects = await _client.GetRecentProjectsAsync();
            RecentProjects.Clear();
            foreach (var project in projects)
            {
                RecentProjects.Add(project);
            }
            RecentProjectsError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the recent projects failed: {ex}");
            RecentProjectsError = $"Could not load the projects: {ex.Message}";
        }
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    /// <summary>Writes anything still waiting to be saved; called when the window closes.</summary>
    public Task FlushAsync() => _settings.FlushAsync();

    public bool HasPendingSave => _settings.HasPendingSave;

    /// <summary>Profile edits not saved as a version; closing asks before dropping them.</summary>
    public bool HasUnsavedProfileChanges => _profiles.HasUnsavedChanges;

    private void ShowPills()
    {
        foreach (var pill in ProviderPills)
        {
            pill.Update(_board.Get(pill.Id), _board.HasChecked);
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
        if (newValue == _profiles)
        {
            PageShowing = ShowProfilesAsync();
        }
        else if (newValue == _newProject)
        {
            // Profiles made or saved since the screen was last open show up in its pickers.
            PageShowing = _newProject.ShowAsync();
        }
    }

    /// <summary>
    /// Workflow files may have been added, or the folder changed in Settings, since the screen was
    /// last open. A folder typed a moment ago may still be waiting to be saved, so that goes first.
    /// </summary>
    private async Task ShowProfilesAsync()
    {
        try
        {
            await _settings.SavePendingAsync();
        }
        catch (Exception ex)
        {
            // Settings shows its own save error; Profiles still opens with what is saved.
            Debug.WriteLine($"Saving settings before opening Profiles failed: {ex}");
        }
        await _profiles.ShowAsync();
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
