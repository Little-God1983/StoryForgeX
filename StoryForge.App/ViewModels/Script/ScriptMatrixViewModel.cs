using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>A fact a segment uses, as the panel lists it: "F01 · 10 · Soul coins hold one soul."</summary>
public sealed record UsedFact(string Id, int Weight, string Statement)
{
    public bool IsMust => Weight >= Fact.MustWeight;
}

/// <summary>One version chip of the selected segment.</summary>
public sealed record SegmentVersionChoice(int Version, bool IsShown, bool IsApproved)
{
    public string Label => $"v{Version}";
}

/// <summary>One row of the matrix: a segment, with its Script cell.</summary>
public sealed partial class SegmentRowViewModel(SegmentView segment, Action<SegmentRowViewModel> select) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Title), nameof(State), nameof(StateText), nameof(VersionText))]
    private SegmentView _segment = segment;

    [ObservableProperty]
    private bool _isSelected;

    public string Id => Segment.Id;

    public string Title => Segment.Title;

    public StageState State => Segment.State;

    /// <summary>"review", "approved", "running" …: the cell's word, as in the canvas legend.</summary>
    public string StateText => Segment.State switch
    {
        StageState.NeedsReview => "review",
        StageState.NotStarted => "—",
        _ => Segment.State.ToString().ToLowerInvariant(),
    };

    public string VersionText => $"v{Segment.Version}";

    [RelayCommand]
    private void Select() => select(this);

    // What screen readers announce for the cell.
    public override string ToString() => $"{Id} script: {StateText}, version {Segment.Version}";
}

/// <summary>
/// The Script stage in the result matrix: the segments as rows, the selected segment in the panel
/// (narration, the facts it uses, versions, Approve, Regenerate, Edit text), the gate bar, and the
/// log while the script is written.
/// </summary>
public sealed partial class ScriptMatrixViewModel(IStoryForgeClient client, Guid projectId) : ObservableObject
{
    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;
    private IReadOnlyDictionary<string, Fact> _facts = new Dictionary<string, Fact>();

    public Guid ProjectId { get; } = projectId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(IsNotStarted), nameof(ShowsLog), nameof(HasSegments))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingCommand), nameof(RegenerateScriptCommand), nameof(CancelCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand), nameof(StartEditCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    public bool IsNotStarted => State == StageState.NotStarted;

    /// <summary>While the whole script is written (or failed), its log is what matters.</summary>
    public bool ShowsLog => IsRunning || (IsFailed && Rows.Count == 0);

    public bool HasSegments => Rows.Count > 0 && !IsRunning;

    [ObservableProperty]
    private string? _error;

    /// <summary>Why the last thing you did did not work; null when it did.</summary>
    [ObservableProperty]
    private string? _actionError;

    public ObservableCollection<SegmentRowViewModel> Rows { get; } = [];

    public ObservableCollection<ActivityLine> Activity { get; } = [];

    /// <summary>"4 of 6 segments approved."</summary>
    [ObservableProperty]
    private string _approvedSummary = "";

    /// <summary>"script about 3:52 · target 4:00".</summary>
    [ObservableProperty]
    private string _lengthSummary = "";

    /// <summary>Why the script is longer than the target; empty when it fits.</summary>
    [ObservableProperty]
    private string _lengthNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedHeading), nameof(SelectedStatus), nameof(SelectedNarration), nameof(SelectedFacts), nameof(SelectedVersions), nameof(SelectedError), nameof(SelectedIsRunning), nameof(SelectedShowsLog))]
    [NotifyCanExecuteChangedFor(nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand), nameof(StartEditCommand), nameof(CancelSegmentCommand))]
    private SegmentRowViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>"S01 — Script".</summary>
    public string SelectedHeading => Selected is null ? "" : $"{Selected.Id} — Script";

    /// <summary>"review · version 2 · about 0:48".</summary>
    public string SelectedStatus => Selected is null
        ? ""
        : $"{Selected.StateText} · version {Selected.Segment.Version} · about {Clock(Selected.Segment.Seconds)}";

    public string SelectedNarration => Selected?.Segment.Narration ?? "";

    public string? SelectedError => Selected?.Segment.Error;

    /// <summary>The selected segment is being written again.</summary>
    public bool SelectedIsRunning => Selected?.State == StageState.Running;

    /// <summary>Its log shows while it is written again, and after that failed.</summary>
    public bool SelectedShowsLog => SelectedIsRunning || (Selected?.State == StageState.Failed && SelectedActivity.Count > 0);

    /// <summary>What writing the selected segment again did.</summary>
    public ObservableCollection<ActivityLine> SelectedActivity { get; } = [];

    public IReadOnlyList<UsedFact> SelectedFacts => Selected is null
        ? []
        : [.. Selected.Segment.FactIds.Select(id => _facts.TryGetValue(id, out var fact) ? new UsedFact(id, fact.Weight, fact.Statement) : new UsedFact(id, 0, "(not on the fact sheet)"))];

    public IReadOnlyList<SegmentVersionChoice> SelectedVersions => Selected is null
        ? []
        : [.. Selected.Segment.Versions.Select(v => new SegmentVersionChoice(v.Version, v.Version == Selected.Segment.Version, v.Version == Selected.Segment.ApprovedVersion))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private bool _isEditing;

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editTitle = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editNarration = "";

    public async Task LoadAsync()
    {
        var mine = ++_loads;
        try
        {
            var view = await client.GetScriptAsync(ProjectId);
            if (mine != _loads)
            {
                return;   // a newer load or a live update came in meanwhile; this view is old
            }
            Apply(view);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the script failed: {ex}");
            ActionError = $"Could not load the script: {ex.Message}";
        }
    }

    /// <summary>A change from the engine for this project, already on the UI thread.</summary>
    public async Task ReceiveAsync(StageUpdate update)
    {
        if (update.ProjectId != ProjectId || update.Stage != PipelineStage.Script)
        {
            return;
        }
        if (update.Key is null && update.State == StageState.Running)
        {
            _loads++;   // a load still on its way would show the state from before this run
            if (State != StageState.Running)
            {
                Activity.Clear();
                State = StageState.Running;
                Error = null;
                IsEditing = false;
            }
            if (update.Activity is { } line)
            {
                Activity.Add(line);
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } segmentLine)
        {
            // A segment's own log line: the panel shows it when that segment is selected. Its state
            // changes come as updates of their own.
            if (update.Key == Selected?.Id)
            {
                SelectedActivity.Add(segmentLine);
            }
            return;
        }
        await LoadAsync();
    }

    private void Apply(ScriptView view)
    {
        _facts = view.Facts.ToDictionary(f => f.Id);
        State = view.State;
        Error = view.Error;
        Activity.Clear();
        foreach (var line in view.Activity)
        {
            Activity.Add(line);
        }

        var shown = Selected?.Id;
        var wasRunning = Selected?.State == StageState.Running;

        // Rows that are still there keep their place and selection; the rest come and go.
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (view.Segments.All(s => s.Id != Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < view.Segments.Count; i++)
        {
            var row = Rows.FirstOrDefault(r => r.Id == view.Segments[i].Id);
            if (row is null)
            {
                Rows.Insert(i, new SegmentRowViewModel(view.Segments[i], Select));
            }
            else
            {
                row.Segment = view.Segments[i];
                if (Rows.IndexOf(row) != i)
                {
                    Rows.Move(Rows.IndexOf(row), i);
                }
            }
        }
        if (Selected is null || !Rows.Contains(Selected))
        {
            Select(Rows.FirstOrDefault());
        }
        RefreshSelection(keepLive: wasRunning && Selected?.Id == shown);

        var approved = view.Segments.Count(s => s.State == StageState.Approved);
        ApprovedSummary = $"{approved} of {view.Segments.Count} segments approved.";
        LengthSummary = view.Segments.Count == 0 ? "" : $"script about {Clock(view.Seconds)} · target {Clock(view.TargetSeconds)}";
        LengthNote = view.LengthNote;
        OnPropertyChanged(nameof(ShowsLog));
        OnPropertyChanged(nameof(HasSegments));
        ApproveRemainingCommand.NotifyCanExecuteChanged();
    }

    private void Select(SegmentRowViewModel? row)
    {
        if (Selected is not null)
        {
            Selected.IsSelected = false;
        }
        Selected = row;
        if (row is not null)
        {
            row.IsSelected = true;
        }
        IsEditing = false;
    }

    /// <summary>The selected row's segment changed in place: the panel reads it again.</summary>
    private void RefreshSelection(bool keepLive)
    {
        ShowSelectedActivity(keepLive);
        foreach (var name in new[] { nameof(SelectedHeading), nameof(SelectedStatus), nameof(SelectedNarration), nameof(SelectedFacts), nameof(SelectedVersions), nameof(SelectedError), nameof(SelectedIsRunning), nameof(SelectedShowsLog) })
        {
            OnPropertyChanged(name);
        }
        ApproveSegmentCommand.NotifyCanExecuteChanged();
        RegenerateSegmentCommand.NotifyCanExecuteChanged();
        StartEditCommand.NotifyCanExecuteChanged();
        CancelSegmentCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(SegmentRowViewModel? value) => ShowSelectedActivity(keepLive: false);

    /// <param name="keepLive">
    /// The segment ran before this load and runs still: a load with fewer lines than came live since
    /// is older than they are, and they stay.
    /// </param>
    private void ShowSelectedActivity(bool keepLive)
    {
        var lines = Selected?.Segment.Activity ?? [];
        if (keepLive && Selected?.State == StageState.Running && lines.Count < SelectedActivity.Count)
        {
            return;
        }
        SelectedActivity.Clear();
        foreach (var line in lines)
        {
            SelectedActivity.Add(line);
        }
    }

    private bool SegmentIdle() => Selected is not null && !IsRunning && Selected.State != StageState.Running;

    private bool CanApproveSegment() => SegmentIdle() && Selected!.State != StageState.Approved;

    [RelayCommand(CanExecute = nameof(CanApproveSegment))]
    private Task ApproveSegmentAsync() =>
        ActAsync("approve the segment", () => client.ApproveSegmentAsync(ProjectId, Selected!.Id, Selected.Segment.Version));

    private bool CanApproveRemaining() => IsReview && Rows.Any(r => r.State != StageState.Approved) && Rows.All(r => r.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemaining))]
    private Task ApproveRemainingAsync() => ActAsync("approve the script", () => client.ApproveScriptAsync(ProjectId));

    [RelayCommand(CanExecute = nameof(SegmentIdle))]
    private Task RegenerateSegmentAsync() => ActAsync("write the segment again", () => client.RegenerateSegmentAsync(ProjectId, Selected!.Id));

    private bool CanRegenerateScript() => !IsRunning;

    /// <summary>Regenerate the whole script; Retry after a failure; Write the script before the first run.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateScript))]
    private Task RegenerateScriptAsync() => ActAsync("write the script", () => client.RegenerateAsync(ProjectId, PipelineStage.Script));

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task CancelAsync() => ActAsync("cancel the script", () => client.CancelAsync(ProjectId, PipelineStage.Script));

    [RelayCommand(CanExecute = nameof(SelectedIsRunning))]
    private Task CancelSegmentAsync() => ActAsync("cancel the segment", () => client.CancelSegmentAsync(ProjectId, Selected!.Id));

    [RelayCommand]
    private Task ShowVersionAsync(int version) =>
        ActAsync("switch the version", () => client.SelectSegmentVersionAsync(ProjectId, Selected!.Id, version));

    [RelayCommand(CanExecute = nameof(SegmentIdle))]
    private void StartEdit()
    {
        EditTitle = Selected!.Title;
        EditNarration = Selected.Segment.Narration;
        IsEditing = true;
    }

    private bool CanSaveEdit() => IsEditing && EditTitle.Trim().Length > 0 && EditNarration.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        var id = Selected!.Id;
        await ActAsync("save the segment", () => client.EditSegmentAsync(ProjectId, id, EditTitle.Trim(), EditNarration.Trim()));
        if (ActionError is null)
        {
            IsEditing = false;   // saved; when it was not, your text stays to try again
        }
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>Runs one action; the engine's update then reloads the matrix. The reason shows when it did not go through.</summary>
    private async Task ActAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            ActionError = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not {what}: {ex}");
            ActionError = $"Could not {what}: {ex.Message}";
        }
    }

    private static string Clock(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}
