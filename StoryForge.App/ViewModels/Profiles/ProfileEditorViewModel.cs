using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Profiles;

/// <summary>
/// One profile on the Profiles screen. Edits go into a draft of the latest version; "Save as vN"
/// stores the draft as a new version. Older versions can be looked at (read only) and restored
/// into the draft, never changed.
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly IStoryForgeClient _client;
    private readonly Dictionary<int, ProfileFormViewModel> _olderVersions = [];
    private IReadOnlyList<string> _templates = [];
    private ProfileContent _baseline;
    private bool _choosingVersion;
    private int _shownVersion;

    public ProfileEditorViewModel(IStoryForgeClient client, ProfileSummary summary, ProfileVersion latest)
    {
        _client = client;
        Id = summary.Id;
        Kind = summary.Kind;
        Name = summary.Name;
        _latestVersion = latest.Version;
        Draft = new ProfileFormViewModel(Kind, latest.Content, isReadOnly: false);
        // As the form gives it back, so a difference in form alone (line breaks, spaces) is no edit.
        _baseline = Draft.ToContent();
        Draft.Changed += (_, _) => UpdateDirty();
        _shown = Draft;
        Versions = [.. Enumerable.Range(1, latest.Version).Reverse()];
        _selectedVersion = latest.Version;
        _shownVersion = latest.Version;
    }

    public Guid Id { get; }

    public ProfileKind Kind { get; }

    public string Name { get; }

    public string KindLabel => ProfileKinds.Label(Kind);

    /// <summary>The editable copy of the latest version.</summary>
    public ProfileFormViewModel Draft { get; }

    /// <summary>What the screen shows: the draft, or an older version read only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewingOlderVersion), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ProfileFormViewModel _shown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText), nameof(SaveNote), nameof(StateText))]
    private int _latestVersion;

    /// <summary>Newest first, for the version picker.</summary>
    public ObservableCollection<int> Versions { get; }

    [ObservableProperty]
    private int? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    /// <summary>Why the last save, version load or import failed; null when all is well.</summary>
    [ObservableProperty]
    private string? _error;

    public bool IsViewingOlderVersion => !ReferenceEquals(Shown, Draft);

    public string SaveText => $"Save as v{LatestVersion + 1}";

    public string SaveNote => $"Saving creates v{LatestVersion + 1}. v{LatestVersion} and older stay as they are.";

    public string StateText =>
        IsViewingOlderVersion ? $"v{_shownVersion} · read only"
        : IsDirty ? $"v{LatestVersion} · unsaved changes"
        : $"v{LatestVersion} · saved";

    /// <summary>The version load the picker started last; tests await it.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Raised after a save added a version.</summary>
    public event EventHandler? Saved;

    public void SetTemplates(IReadOnlyList<string> templates)
    {
        _templates = templates;
        Draft.SetTemplates(templates);
        foreach (var form in _olderVersions.Values)
        {
            form.SetTemplates(templates);
        }
    }

    private bool CanSave() => IsDirty && !IsViewingOlderVersion && !IsSaving && !Draft.HasAnyErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        Error = null;
        try
        {
            var content = Draft.ToContent();
            var saved = await _client.SaveProfileVersionAsync(Id, content);
            // What was sent, not what came back: the user may have typed on while it saved.
            _baseline = content;
            if (saved.Version > LatestVersion)
            {
                LatestVersion = saved.Version;
                Versions.Insert(0, saved.Version);
                // An older version picked while the save ran stays on screen; otherwise the
                // draft is now the new latest version.
                if (!IsViewingOlderVersion)
                {
                    ShowDraft();
                }
                Saved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Error = ex is ArgumentException ? ex.Message : $"Could not save: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
            UpdateDirty();
        }
    }

    /// <summary>Copies the older version on screen into the draft; saving it makes it the newest.</summary>
    [RelayCommand]
    private void RestoreShownVersion()
    {
        if (IsViewingOlderVersion)
        {
            Draft.Load(Shown.ToContent());
            ShowDraft();
        }
    }

    [RelayCommand]
    private void ShowLatest() => ShowDraft();

    [RelayCommand]
    private void RemoveReferenceFile(string path) => Draft.ReferenceFiles.Remove(path);

    /// <summary>Copies the picked files into the engine's store and adds them to the draft.</summary>
    public async Task AddReferenceFilesAsync(IEnumerable<string> paths)
    {
        Error = null;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var stored = await _client.ImportReferenceFileAsync(path);
                if (!Draft.ReferenceFiles.Contains(stored))
                {
                    Draft.ReferenceFiles.Add(stored);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }
        if (failures.Count > 0)
        {
            Error = $"Could not add {string.Join(", ", failures)}.";
        }
    }

    partial void OnSelectedVersionChanged(int? value)
    {
        // Null comes from the picker while its list changes; the shown version stays.
        if (value is { } version && !_choosingVersion)
        {
            Loading = ShowVersionAsync(version);
        }
    }

    private async Task ShowVersionAsync(int version)
    {
        if (version == LatestVersion)
        {
            ShowDraft();
            return;
        }
        Error = null;
        try
        {
            if (!_olderVersions.TryGetValue(version, out var form))
            {
                var saved = await _client.GetProfileVersionAsync(Id, version);
                form = new ProfileFormViewModel(Kind, saved.Content, isReadOnly: true);
                form.SetTemplates(_templates);
                _olderVersions[version] = form;
            }
            // Another version may have been picked while this one loaded.
            if (SelectedVersion == version)
            {
                _shownVersion = version;
                Shown = form;
                OnPropertyChanged(nameof(StateText));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading v{version} of profile {Id} failed: {ex}");
            Error = $"Could not load v{version}: {ex.Message}";
            // The picker goes back to what is on screen, so it never names a version not shown.
            if (SelectedVersion == version)
            {
                ChooseVersion(_shownVersion);
            }
        }
    }

    private void ShowDraft()
    {
        _shownVersion = LatestVersion;
        ChooseVersion(LatestVersion);
        Shown = Draft;
        OnPropertyChanged(nameof(StateText));
    }

    private void ChooseVersion(int version)
    {
        _choosingVersion = true;
        try
        {
            SelectedVersion = version;
        }
        finally
        {
            _choosingVersion = false;
        }
    }

    private void UpdateDirty()
    {
        IsDirty = !ProfileFormViewModel.SameContent(Draft.ToContent(), _baseline);
        SaveCommand.NotifyCanExecuteChanged();
    }
}
