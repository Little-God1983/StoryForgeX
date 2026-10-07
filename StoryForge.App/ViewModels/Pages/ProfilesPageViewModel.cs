using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Profiles;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>
/// The Profiles screen: every profile, grouped by kind, and the editor for the selected one.
/// Unsaved edits stay with their profile while you look at another one.
/// </summary>
public sealed partial class ProfilesPageViewModel : PageViewModel
{
    private const string BaseBreadcrumb = "Settings / Profiles";

    private readonly IStoryForgeClient _client;
    private readonly Dictionary<Guid, ProfileEditorViewModel> _editors = [];
    private IReadOnlyList<string> _templates = [];
    private int _templateReads;

    public ProfilesPageViewModel(IStoryForgeClient client)
        : base("Profiles", BaseBreadcrumb, "")
    {
        _client = client;
    }

    /// <summary>By kind, then name; the view groups them under their kind.</summary>
    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    [ObservableProperty]
    private ProfileListItemViewModel? _selectedProfile;

    [ObservableProperty]
    private ProfileEditorViewModel? _editor;

    /// <summary>Why the list or a profile could not be loaded; null when all is well.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>The profile load the list started last; tests await it.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public bool HasUnsavedChanges => _editors.Values.Any(e => e.IsDirty);

    // ── "+ New profile" ──

    public static IReadOnlyList<Settings.Choice<ProfileKind>> KindChoices => ProfileKinds.Choices;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private ProfileKind _newKind = ProfileKind.Image;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newName = "";

    [ObservableProperty]
    private string? _createError;

    public async Task LoadAsync()
    {
        try
        {
            var profiles = await _client.GetProfilesAsync();
            Profiles.Clear();
            foreach (var summary in profiles)
            {
                Profiles.Add(new ProfileListItemViewModel(summary));
            }
            OnPropertyChanged(nameof(HasProfiles));
            LoadError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading profiles failed: {ex}");
            LoadError = $"Could not load the profiles: {ex.Message}";
        }
        await RefreshTemplatesAsync();
    }

    /// <summary>Reads the workflow files again; the folder may have changed in Settings.</summary>
    public async Task RefreshTemplatesAsync()
    {
        // Reads can overlap (startup and opening the screen); one that started earlier but
        // finishes later may hold an old folder's files, so only the newest read counts.
        var read = ++_templateReads;
        IReadOnlyList<string> templates;
        try
        {
            templates = await _client.GetWorkflowTemplatesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading the workflow templates failed: {ex}");
            templates = [];
        }
        if (read != _templateReads)
        {
            return;
        }
        _templates = templates;
        foreach (var editor in _editors.Values)
        {
            editor.SetTemplates(_templates);
        }
    }

    [RelayCommand]
    private void StartCreate()
    {
        NewKind = SelectedProfile?.Kind ?? ProfileKind.Image;
        NewName = "";
        CreateError = null;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        CreateError = null;
    }

    private bool CanCreate() => NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        CreateError = null;
        ProfileSummary created;
        try
        {
            created = await _client.CreateProfileAsync(NewKind, NewName);
        }
        catch (Exception ex)
        {
            CreateError = ex is ArgumentException ? ex.Message : $"Could not create the profile: {ex.Message}";
            return;
        }

        var item = new ProfileListItemViewModel(created);
        Profiles.Insert(InsertIndex(item), item);
        OnPropertyChanged(nameof(HasProfiles));
        IsCreating = false;
        SelectedProfile = item;
        await Loading;
    }

    partial void OnSelectedProfileChanged(ProfileListItemViewModel? value)
    {
        // Null comes from Ctrl+click on the selected entry; the view puts the selection back.
        if (value is not null)
        {
            Loading = OpenAsync(value);
        }
    }

    private async Task OpenAsync(ProfileListItemViewModel item)
    {
        if (!_editors.TryGetValue(item.Id, out var editor))
        {
            ProfileVersion latest;
            try
            {
                latest = await _client.GetProfileVersionAsync(item.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Loading profile {item.Id} failed: {ex}");
                if (SelectedProfile == item)
                {
                    Editor = null;
                    LoadError = $"Could not load {item.Name}: {ex.Message}";
                    Breadcrumb = BaseBreadcrumb;
                }
                return;
            }
            // A click on another entry while this one loaded may have made an editor already.
            if (!_editors.TryGetValue(item.Id, out editor))
            {
                editor = new ProfileEditorViewModel(_client, new ProfileSummary(item.Id, item.Kind, item.Name, latest.Version), latest);
                editor.SetTemplates(_templates);
                editor.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ProfileEditorViewModel.IsDirty))
                    {
                        item.HasUnsavedChanges = editor.IsDirty;
                    }
                };
                editor.Saved += (_, _) => item.LatestVersion = editor.LatestVersion;
                _editors[item.Id] = editor;
            }
        }
        if (SelectedProfile == item)
        {
            LoadError = null;
            Editor = editor;
            Breadcrumb = $"{BaseBreadcrumb} / {item.Name}";
        }
    }

    private int InsertIndex(ProfileListItemViewModel item)
    {
        var index = 0;
        while (index < Profiles.Count && Compare(Profiles[index], item) <= 0)
        {
            index++;
        }
        return index;
    }

    private static int Compare(ProfileListItemViewModel a, ProfileListItemViewModel b) =>
        a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
}
