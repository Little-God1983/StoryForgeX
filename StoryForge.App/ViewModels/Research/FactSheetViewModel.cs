using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;
using ActivityKind = StoryForge.Client.ActivityKind;

namespace StoryForge.App.ViewModels.Research;

/// <summary>One version button above the facts: "v1", "v2".</summary>
public sealed record VersionChoice(int Version, bool IsShown, bool IsApproved)
{
    public string Label => $"v{Version}";
}

/// <summary>
/// The Research stage of the open project: the live log while it runs, the reason when it failed,
/// and the fact sheet to review: weights, leaving facts out, rewording them, approving a version.
/// </summary>
public sealed partial class FactSheetViewModel : ObservableObject
{
    private readonly IStoryForgeClient _client;
    private readonly Action<string> _openUrl;
    // One change at a time: each builds on the version the last one made.
    private readonly SemaphoreSlim _changing = new(1, 1);

    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;

    public FactSheetViewModel(IStoryForgeClient client, Guid projectId, IReadOnlyList<string> sources, Action<string> openUrl)
    {
        _client = client;
        _openUrl = openUrl;
        ProjectId = projectId;
        Sources = sources;
    }

    public Guid ProjectId { get; }

    /// <summary>"Approve and continue" went through; the screen goes back to the matrix.</summary>
    public event EventHandler? Approved;

    /// <summary>The project's sources, for "Only … can be reached".</summary>
    public IReadOnlyList<string> Sources { get; }

    public string SourcesText => Sources.Count == 0 ? "no sources" : string.Join(" and ", Sources);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(IsNotStarted), nameof(ShowsLog), nameof(ShowsSheet))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RegenerateCommand), nameof(CancelCommand), nameof(StartEditCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    public bool IsNotStarted => State == StageState.NotStarted;

    /// <summary>Running or failed: the log of what the research did is what matters.</summary>
    public bool ShowsLog => IsRunning || IsFailed;

    public bool ShowsSheet => !ShowsLog && Version is not null;

    /// <summary>Why the last run failed.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Why the last thing you did (approve, change a fact) did not work; null when it did.</summary>
    [ObservableProperty]
    private string? _actionError;

    /// <summary>The version on screen; approving approves this one, changes build on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSheet))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private int? _version;

    [ObservableProperty]
    private int? _approvedVersion;

    [ObservableProperty]
    private IReadOnlyList<VersionChoice> _versions = [];

    /// <summary>"v2 · your edits of v1 · approved".</summary>
    [ObservableProperty]
    private string _versionNote = "";

    public ObservableCollection<FactRowViewModel> Facts { get; } = [];

    public ObservableCollection<FactRowViewModel> LeftOutFacts { get; } = [];

    public bool HasLeftOutFacts => LeftOutFacts.Count > 0;

    /// <summary>"6 facts in use, 1 left out, 1 marked must."</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFact))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand), nameof(OpenSourceCommand))]
    private FactRowViewModel? _selectedFact;

    public bool HasSelectedFact => SelectedFact is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private bool _isEditing;

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editText = "";

    public ObservableCollection<ActivityLine> Activity { get; } = [];

    /// <summary>Per site, what the research did there: "bg3.wiki · 2 searches · 4 pages".</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _siteSummary = [];

    /// <summary>"1 page refused (not a project source)"; empty when none was.</summary>
    [ObservableProperty]
    private string _refusedSummary = "";

    public async Task LoadAsync(int? version = null)
    {
        var mine = ++_loads;
        try
        {
            var view = await _client.GetFactSheetAsync(ProjectId, version);
            if (mine != _loads)
            {
                return;   // a newer load or a live update came in meanwhile; this view is old
            }
            Apply(view);
            ActionError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the fact sheet failed: {ex}");
            ActionError = $"Could not load the fact sheet: {ex.Message}";
        }
    }

    /// <summary>A change from the engine for this project, already on the UI thread.</summary>
    public async Task ReceiveAsync(StageUpdate update)
    {
        if (update.ProjectId != ProjectId || update.Stage != PipelineStage.Research)
        {
            return;
        }
        if (update.State == StageState.Running)
        {
            _loads++;   // a load still on its way would show the state from before this run
            if (State != StageState.Running)
            {
                // A new run: its log starts empty.
                Activity.Clear();
                State = StageState.Running;
                Error = null;
                IsEditing = false;
            }
            if (update.Activity is { } line)
            {
                Activity.Add(line);
                Summarize();
            }
            return;
        }
        // The run ended or the sheet changed: read it all again, on the newest version.
        await LoadAsync();
    }

    private void Apply(FactSheetView view)
    {
        State = view.State;
        Error = view.Error;
        Version = view.Version;
        ApprovedVersion = view.ApprovedVersion;
        Versions = [.. view.Versions.Select(v => new VersionChoice(v.Version, v.Version == view.Version, v.Version == view.ApprovedVersion))];
        var shown = view.Versions.FirstOrDefault(v => v.Version == view.Version);
        VersionNote = shown is null ? "" : string.Join(" · ",
            new[]
            {
                $"v{shown.Version}",
                shown.Origin == VersionOrigin.Edited ? $"your edits of v{shown.BasedOn}" : $"researched {shown.CreatedAt.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)}",
                shown.Version == view.ApprovedVersion ? "approved" : "",
            }.Where(part => part.Length > 0));

        var facts = view.Sheet?.Facts ?? [];
        Sync(Facts, facts.Where(f => !f.LeftOut));
        Sync(LeftOutFacts, facts.Where(f => f.LeftOut));
        OnPropertyChanged(nameof(HasLeftOutFacts));
        var selectedId = SelectedFact?.Id;
        Select(Facts.Concat(LeftOutFacts).FirstOrDefault(f => f.Id == selectedId) ?? Facts.FirstOrDefault() ?? LeftOutFacts.FirstOrDefault());

        var musts = facts.Count(f => !f.LeftOut && f.Weight >= Fact.MustWeight);
        Summary = $"{Facts.Count} {(Facts.Count == 1 ? "fact" : "facts")} in use, {LeftOutFacts.Count} left out, {musts} marked must.";

        Activity.Clear();
        foreach (var line in view.Activity)
        {
            Activity.Add(line);
        }
        Summarize();
    }

    /// <summary>Keeps the rows that are still there (and their place on screen), updates them, adds and removes the rest.</summary>
    private void Sync(ObservableCollection<FactRowViewModel> rows, IEnumerable<Fact> facts)
    {
        var wanted = facts.ToList();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(f => f.Id == rows[i].Id))
            {
                rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Id == wanted[i].Id);
            if (row is null)
            {
                row = new FactRowViewModel(wanted[i], ChangeAsync, Select);
                rows.Insert(i, row);
            }
            else
            {
                row.Update(wanted[i]);
                var at = rows.IndexOf(row);
                if (at != i)
                {
                    rows.Move(at, i);
                }
            }
        }
    }

    private void Select(FactRowViewModel? row)
    {
        if (SelectedFact == row)
        {
            return;
        }
        if (SelectedFact is not null)
        {
            SelectedFact.IsSelected = false;
        }
        SelectedFact = row;
        if (row is not null)
        {
            row.IsSelected = true;
        }
        IsEditing = false;
    }

    private void Summarize()
    {
        static string Site(string text) => text.Split([' ', '/'], 2)[0];

        SiteSummary =
        [
            .. Activity.Where(a => a.Kind is ActivityKind.Search or ActivityKind.Fetch)
                .GroupBy(a => Site(a.Text))
                .Select(g =>
                {
                    var searches = g.Count(a => a.Kind == ActivityKind.Search);
                    var pages = g.Count(a => a.Kind == ActivityKind.Fetch);
                    return $"{g.Key} · {searches} {(searches == 1 ? "search" : "searches")} · {pages} {(pages == 1 ? "page" : "pages")}";
                }),
        ];
        var refused = Activity.Count(a => a.Kind == ActivityKind.Refused);
        RefusedSummary = refused == 0 ? "" : $"{refused} {(refused == 1 ? "request" : "requests")} refused (not a project source)";
    }

    private async Task ChangeAsync(FactRowViewModel row, Func<FactRowViewModel, FactChange?> make)
    {
        await _changing.WaitAsync();
        try
        {
            // Worked out now, after the changes before it came back.
            if (Version is not { } version || make(row) is not { } change)
            {
                return;
            }
            Apply(await _client.ChangeFactAsync(ProjectId, version, row.Id, change));
            ActionError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Changing {row.Id} failed: {ex}");
            ActionError = $"Could not change {row.Id}: {ex.Message}";
        }
        finally
        {
            _changing.Release();
        }
    }

    private bool CanApprove() => IsReview && Version is not null;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        // Reloaded only when it went through: a reload would clear the reason it did not.
        if (await RunAsync("approve the fact sheet", () => _client.ApproveAsync(ProjectId, PipelineStage.Research, Version!.Value)))
        {
            await LoadAsync();
            Approved?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanRegenerate() => !IsRunning;

    /// <summary>Regenerate after a result, Retry after a failure, Run research before the first run.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync() => RunAsync("start the research", () => _client.RegenerateAsync(ProjectId, PipelineStage.Research));

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task CancelAsync() => RunAsync("cancel the research", () => _client.CancelAsync(ProjectId, PipelineStage.Research));

    [RelayCommand]
    private Task ShowVersionAsync(int version) => LoadAsync(version);

    private bool CanEdit() => SelectedFact is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StartEdit()
    {
        EditText = SelectedFact!.Statement;
        IsEditing = true;
    }

    private bool CanSaveEdit() => IsEditing && EditText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        var row = SelectedFact!;
        var text = EditText.Trim();
        IsEditing = false;
        if (text != row.Statement)
        {
            await ChangeAsync(row, _ => new FactChange(Statement: text));
        }
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    private bool CanOpenSource() => SelectedFact is not null;

    [RelayCommand(CanExecute = nameof(CanOpenSource))]
    private void OpenSource()
    {
        try
        {
            _openUrl(SelectedFact!.SourceUrl);
        }
        catch (Exception ex)
        {
            ActionError = $"Could not open {SelectedFact!.SourceUrl}: {ex.Message}";
        }
    }

    /// <summary>Runs one action; false (and the reason on screen) when it did not go through.</summary>
    private async Task<bool> RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            ActionError = null;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not {what}: {ex}");
            ActionError = $"Could not {what}: {ex.Message}";
            return false;
        }
    }
}
