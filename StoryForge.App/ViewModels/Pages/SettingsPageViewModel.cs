using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Settings;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>One entry of the Settings side menu.</summary>
/// <param name="Placeholder">What an unbuilt section says; null for a built one.</param>
public sealed record SettingsSection(string Title, string? Placeholder);

/// <summary>
/// The Settings screen. There is no Save button: every edit is saved shortly after the last
/// keystroke, and the provider checks run again so each card shows whether its new values work.
/// </summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly IStoryForgeClient _client;
    private readonly ProviderStatusBoard _board;
    private readonly TimeSpan _saveDelay;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _pendingDelay;
    private bool _savePending;
    private bool _loading;

    public SettingsPageViewModel(IStoryForgeClient client, ProviderStatusBoard board, TimeSpan saveDelay)
        : base("Settings", "Settings / Providers", "")
    {
        _client = client;
        _board = board;
        _saveDelay = saveDelay;

        ClaudeCli = new ClaudeCliCardViewModel();
        LmStudio = new LmStudioCardViewModel(client);
        ComfyUi = new ComfyUiCardViewModel();
        Ffmpeg = new FfmpegCardViewModel();
        Cards = [ClaudeCli, LmStudio, ComfyUi, Ffmpeg];
        foreach (var card in Cards)
        {
            card.Edited += (_, _) => ScheduleSave();
        }
        board.StatusesChanged += (_, _) => ShowStatuses();
        LmStudio.ApiTokenChanged += async (_, _) => await board.RefreshAsync();

        Sections =
        [
            Providers,
            new("Storage", "Content Automator X comes with issue #15."),
            new("Assembly & export", "DaVinci Resolve comes with issue #17. FFmpeg is set up under Providers."),
            PathsAndCache,
            new("Engine host", "The engine runs inside the app. Server mode is phase 2 (issue #36)."),
        ];
        _selectedSection = Providers;
    }

    public static SettingsSection Providers { get; } = new("Providers", null);

    public static SettingsSection PathsAndCache { get; } = new("Paths & cache", null);

    public IReadOnlyList<SettingsSection> Sections { get; }

    [ObservableProperty]
    private SettingsSection _selectedSection;

    public ClaudeCliCardViewModel ClaudeCli { get; }

    public LmStudioCardViewModel LmStudio { get; }

    public ComfyUiCardViewModel ComfyUi { get; }

    public FfmpegCardViewModel Ffmpeg { get; }

    public IReadOnlyList<ProviderCardViewModel> Cards { get; }

    /// <summary>The folder typed by the user; empty means the engine's default.</summary>
    [ObservableProperty]
    private string _projectsFolder = "";

    /// <summary>The folder actually in use, shown under the field.</summary>
    [ObservableProperty]
    private string _effectiveProjectsFolder = "";

    /// <summary>Why the last save was refused, or null.</summary>
    [ObservableProperty]
    private string? _saveError;

    /// <summary>The save scheduled by the last edit; completes once it is written (or skipped).</summary>
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Writes an edit that is still waiting for its delay, now. The window calls this on close, so
    /// a change typed just before closing is not lost.
    /// </summary>
    public async Task FlushAsync()
    {
        _pendingDelay?.Cancel();
        await SaveNowAsync();
        await PendingSave;
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var settings = await _client.GetSettingsAsync();
            foreach (var card in Cards)
            {
                card.Load(settings);
            }
            ProjectsFolder = settings.Paths.ProjectsFolder;
            EffectiveProjectsFolder = await _client.GetEffectiveProjectsFolderAsync();
            await LmStudio.LoadApiTokenStateAsync();
            ShowStatuses();
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedSectionChanged(SettingsSection value) => Breadcrumb = $"Settings / {value.Title}";

    partial void OnProjectsFolderChanged(string value) => ScheduleSave();

    private void ShowStatuses()
    {
        foreach (var card in Cards)
        {
            card.UpdateStatus(_board.Get(card.Id), _board.HasChecked);
        }
    }

    private void ScheduleSave()
    {
        if (_loading)
        {
            return;
        }
        _savePending = true;
        _pendingDelay?.Cancel();
        _pendingDelay = new CancellationTokenSource();
        PendingSave = SaveAfterDelayAsync(_pendingDelay.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        if (_saveDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_saveDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;   // A newer edit, or a flush, takes over.
            }
        }
        await SaveNowAsync();
    }

    /// <summary>
    /// Writes the current fields, one save at a time. A card with an invalid field keeps its last
    /// saved values (its field shows the error); every other card's edits are written.
    /// </summary>
    private async Task SaveNowAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            if (!_savePending)
            {
                return;
            }
            _savePending = false;

            var saved = await _client.GetSettingsAsync();
            var settings = Cards.Where(card => !card.HasErrors)
                .Aggregate(saved, (current, card) => card.ApplyTo(current))
                with { Paths = new PathSettings(ProjectsFolder.Trim()) };
            if (settings == saved)
            {
                return;   // Only invalid fields changed: nothing valid to write.
            }

            await _client.SaveSettingsAsync(settings);
            SaveError = null;
            EffectiveProjectsFolder = await _client.GetEffectiveProjectsFolderAsync();
        }
        catch (ArgumentException ex)
        {
            SaveError = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            SaveError = $"Could not save: {ex.Message}";
            return;
        }
        finally
        {
            _saveLock.Release();
        }
        await _board.RefreshAsync();
    }
}
