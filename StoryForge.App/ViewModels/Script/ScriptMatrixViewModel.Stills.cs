using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// A shot's Still cell: its state and version, or "queued" while Regenerate has it waiting for
/// Continue pipeline. While every still is made, a cell still to come shows running.
/// </summary>
/// <param name="label">The shot's label now, "S01·2", for screen readers.</param>
public sealed partial class StillCellViewModel(Action<StillCellViewModel> select, Func<string> label) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StateText), nameof(VersionText), nameof(HasStill), nameof(IsQueued), nameof(AccessibleName))]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private StillView? _still;

    /// <summary>The Stills stage as a whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StateText), nameof(AccessibleName))]
    private StageState _stageState;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasStill => Still is not null;

    /// <summary>It waits in the batch; while the batch is made it shows running instead.</summary>
    public bool IsQueued => Still is { Queued: true, State: not StageState.Running };

    /// <summary>A run is to make it: it waits in the batch, or was never made (its cell has no version).</summary>
    public bool IsWaiting => Still is null || Still.Queued || Still.Still is null;

    // A shot the run never got to stays "—" when the run failed: the bar says where it failed.
    public StageState State => Still?.State ?? (StageState == StageState.Running ? StageState.Running : StageState.NotStarted);

    public string StateText => ScriptMatrixViewModel.Word(State);

    /// <summary>"v2", or "queued" while it waits in the batch.</summary>
    public string VersionText => IsQueued ? "queued" : Still is { Version: > 0 } still ? $"v{still.Version}" : "";

    [RelayCommand(CanExecute = nameof(HasStill))]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the cell; it follows the cell as it changes.</summary>
    public string AccessibleName => $"{label()} still: {StateText}{(Still is { Version: > 0 } still ? $", version {still.Version}" : "")}{(IsQueued ? ", queued" : "")}";

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The stills in the matrix (board 2b): each shot's Still cell, the Stills gate bar with Approve
/// remaining and Continue pipeline, and the Still panel: the picture, its prompt and how it was made,
/// Regenerate (into the batch), Edit prompt…, Replace with file….
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStillsRunning), nameof(IsStillsReview), nameof(IsStillsApproved), nameof(IsStillsFailed), nameof(CanMakeStills))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingStillsCommand), nameof(ContinuePipelineCommand), nameof(CancelStillsCommand),
        nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private StageState _stillsState;

    public bool IsStillsRunning => StillsState == StageState.Running;

    public bool IsStillsReview => StillsState is StageState.NeedsReview or StageState.Stale;

    public bool IsStillsApproved => StillsState == StageState.Approved;

    public bool IsStillsFailed => StillsState == StageState.Failed;

    /// <summary>The references are approved and the stills were never made: a project from before the Stills stage starts them here.</summary>
    public bool CanMakeStills => StillsState == StageState.NotStarted && _referencesApproved && HasShots;

    private bool _referencesApproved;

    [ObservableProperty]
    private string? _stillsError;

    /// <summary>What making the stills did; its last line shows in the bar while it runs.</summary>
    public ObservableCollection<ActivityLine> StillsActivity { get; } = [];

    /// <summary>"S01·2: sent to ComfyUI".</summary>
    [ObservableProperty]
    private string _stillsProgress = "";

    /// <summary>"6 of 10 shots approved."</summary>
    [ObservableProperty]
    private string _stillsApprovedSummary = "";

    /// <summary>"2 stills wait in the batch, 1 shot has no still yet: Continue pipeline makes them."; empty when nothing waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueuedStills))]
    private string _queuedSummary = "";

    public bool HasQueuedStills => QueuedSummary.Length > 0;

    /// <summary>Continue pipeline has something to make: stills in the batch, or shots with no still yet.</summary>
    private bool HasWaitingStills => StillsState != StageState.NotStarted && StillCells.Any(c => c.IsWaiting);

    private IEnumerable<StillCellViewModel> StillCells => Rows.SelectMany(r => r.Shots).Select(s => s.Still);

    private bool CanApproveRemainingStills() => IsStillsReview && StillCells.Any(c => c.HasStill) && StillCells.All(c => c.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemainingStills))]
    private Task ApproveRemainingStillsAsync() => ActAsync("approve the stills", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.Stills));

    // The batch runs when nothing of the stills runs; a project whose stills were never made starts them.
    private bool CanContinuePipeline() =>
        CanMakeStills || (_referencesApproved && !IsStillsRunning && (HasWaitingStills || IsStillsFailed) && StillCells.All(c => c.State != StageState.Running));

    /// <summary>Continue pipeline: one run makes the stills in the batch and those of shots with none yet.</summary>
    [RelayCommand(CanExecute = nameof(CanContinuePipeline))]
    private Task ContinuePipelineAsync() => ActAsync("continue the pipeline", () => client.ContinuePipelineAsync(ProjectId));

    [RelayCommand(CanExecute = nameof(IsStillsRunning))]
    private Task CancelStillsAsync() => ActAsync("cancel the stills", () => client.CancelAsync(ProjectId, PipelineStage.Stills));

    private async Task ReceiveStillsAsync(StageUpdate update)
    {
        if (update.Key is null && update.State == StageState.Running)
        {
            var starting = StillsState != StageState.Running;
            if (starting)
            {
                _loads++;   // a load still on its way would show the state from before this run
                StillsActivity.Clear();
                StillsProgress = "";
                StillsState = StageState.Running;
                StillsError = null;
                foreach (var cell in StillCells)
                {
                    // The ones this run makes (in the batch, or with none yet) show their new state as each is done.
                    cell.StageState = StageState.Running;
                    cell.Still = cell.Still is { } made && cell.IsWaiting ? made with { State = StageState.Running } : cell.Still;
                }
                RefreshSelection(keepLive: false);
            }
            if (update.Activity is { } line)
            {
                StillsActivity.Add(line);
                StillsProgress = line.Text;
            }
            if (starting)
            {
                await LoadAsync();
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } stillLine)
        {
            if (IsStillPanel && update.Key == SelectedShot?.Key)
            {
                SelectedActivity.Add(stillLine);
            }
            return;
        }
        await LoadAsync();
    }

    private void ApplyStills(StillsView stills)
    {
        var keepLive = StillsState == StageState.Running && stills.State == StageState.Running && stills.Activity.Count < StillsActivity.Count;
        _referencesApproved = stills.ReferencesApproved;
        StillsState = stills.State;
        StillsError = stills.Error;
        if (!keepLive)
        {
            StillsActivity.Clear();
            foreach (var line in stills.Activity)
            {
                StillsActivity.Add(line);
            }
            StillsProgress = stills.Activity.Count > 0 ? stills.Activity[^1].Text : "";
        }
        foreach (var shot in Rows.SelectMany(r => r.Shots))
        {
            shot.Still.StageState = stills.State;
            shot.Still.Still = stills.Stills.FirstOrDefault(s => s.Key == shot.Key);
        }
        foreach (var row in Rows)
        {
            row.ShotsChanged();
        }
        var shots = Rows.Sum(r => r.Shots.Count);
        StillsApprovedSummary = $"{StillCells.Count(c => c.State == StageState.Approved)} of {shots} shots approved.";
        var queued = StillCells.Count(c => c.IsQueued);
        var missing = stills.State == StageState.NotStarted ? 0 : StillCells.Count(c => c.Still?.Still is null && c.State != StageState.Running);
        var parts = new List<string>();
        if (queued > 0)
        {
            parts.Add($"{queued} {(queued == 1 ? "still waits" : "stills wait")} in the batch");
        }
        if (missing > 0)
        {
            parts.Add($"{missing} {(missing == 1 ? "shot has" : "shots have")} no still yet");
        }
        QueuedSummary = parts.Count == 0 ? "" : $"{string.Join(", ", parts)}: Continue pipeline makes {(queued + missing == 1 ? "it" : "them")}.";
        OnPropertyChanged(nameof(CanMakeStills));
        ApproveRemainingStillsCommand.NotifyCanExecuteChanged();
        ContinuePipelineCommand.NotifyCanExecuteChanged();
    }

    private void SelectStill(StillCellViewModel cell)
    {
        var shot = Rows.SelectMany(r => r.Shots).FirstOrDefault(s => s.Still == cell);
        Select(Rows.FirstOrDefault(r => shot is not null && r.Shots.Contains(shot)), PipelineStage.Stills, shot);
    }

    // ── The Still panel ──

    /// <summary>The selected still, when the panel shows a Still cell that has one.</summary>
    private StillView? SelectedStill => IsStillPanel ? SelectedShot?.Still.Still : null;

    /// <summary>The picture to show; null before it was made, or when the file is gone.</summary>
    public string? StillPicture => SelectedStill?.Path;

    /// <summary>The still was made but its file is gone from the projects folder.</summary>
    public bool StillIsMissing => SelectedStill is { Still: not null, Path: null };

    /// <summary>"Prompt (from the storyboard + profile)", "Your prompt", "Your own picture".</summary>
    public string StillPromptTitle => SelectedStill?.Still switch
    {
        { YourFile: true } => "Your own picture (the prompt it stands in for)",
        { YourPrompt: true } => "Your prompt",
        _ => "Prompt (from the storyboard + the still profile)",
    };

    public string StillPrompt => SelectedStill?.Still?.Prompt ?? "";

    /// <summary>"seed 184223 · 1344×768 · wf qwen.json · inputs a41f9c2e…": what board 2 showed under the prompt.</summary>
    public string StillDetails => SelectedStill is { Still: { } still } view
        ? string.Join(" · ", new[]
        {
            still.YourFile ? "" : $"seed {still.Seed}",
            $"{still.Size.Width}×{still.Size.Height}",
            still.Workflow.Length > 0 ? $"wf {still.Workflow}" : "",
            view.InputHash.Length > 0 ? $"inputs {view.InputHash[..Math.Min(8, view.InputHash.Length)]}…" : "",
        }.Where(p => p.Length > 0))
        : "";

    /// <summary>"Made from R01/1 · starts from the still of S01·1"; empty for your own picture.</summary>
    public string StillMadeFrom
    {
        get
        {
            if (SelectedStill?.Still is not { YourFile: false } still)
            {
                return "";
            }
            var parts = new List<string>();
            if (still.ContinuesFrom is { } from)
            {
                var label = Rows.SelectMany(r => r.Shots).FirstOrDefault(s => s.Key == from)?.Label ?? from;
                parts.Add($"starts from the still of {label} (continues it)");
            }
            parts.Add(still.References.Count > 0 ? $"reference images {string.Join(", ", still.References)}" : "no reference image");
            return "Made from " + string.Join(" · ", parts);
        }
    }

    /// <summary>"Waits in the batch: Continue pipeline makes it again (with your prompt)."; empty when it is not queued.</summary>
    public string StillQueuedText => SelectedStill is { Queued: true } queued
        ? $"Waits in the batch: Continue pipeline makes it again{(queued.QueuedPrompt is null ? "" : " with your prompt")}."
        : "";

    public bool StillIsQueued => SelectedStill?.Queued == true;

    /// <summary>Edit prompt… is open: your prompt for the still, to queue it with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditingAny))]
    [NotifyCanExecuteChangedFor(nameof(QueuePromptCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private bool _isEditingPrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueuePromptCommand))]
    private string _editPrompt = "";

    private bool CanEditStill() => IsStillPanel && SegmentIdle() && !IsEditingPrompt;

    [RelayCommand(CanExecute = nameof(CanEditStill))]
    private void StartEditPrompt()
    {
        EditPrompt = SelectedStill?.QueuedPrompt ?? StillPrompt;
        IsEditingPrompt = true;
    }

    private bool CanQueuePrompt() => IsEditingPrompt && EditPrompt.Trim().Length > 0;

    /// <summary>"Make it with this prompt": the still waits in the batch with your prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanQueuePrompt))]
    private async Task QueuePromptAsync()
    {
        var key = SelectedShot!.Key;
        await ActAsync("queue the still", () => client.QueueStillAsync(ProjectId, key, EditPrompt.Trim()));
        if (ActionError is null)
        {
            IsEditingPrompt = false;   // queued; when it was not, your prompt stays to try again
        }
    }

    [RelayCommand]
    private void CancelEditPrompt() => IsEditingPrompt = false;

    [RelayCommand(CanExecute = nameof(CanEditStill))]
    private Task UnqueueStillAsync()
    {
        var key = SelectedShot!.Key;
        return ActAsync("take the still out of the batch", () => client.UnqueueStillAsync(ProjectId, key));
    }

    /// <summary>Replace with file…: your own picture as the still's next version.</summary>
    [RelayCommand(CanExecute = nameof(CanEditStill))]
    private async Task ReplaceStillAsync()
    {
        var key = SelectedShot!.Key;
        if (pickPicture?.Invoke() is not { } path)
        {
            return;   // no file picked
        }
        await ActAsync("replace the still", () => client.ReplaceStillAsync(ProjectId, key, path));
    }

    private void RefreshStillPanel()
    {
        foreach (var name in new[] { nameof(StillPicture), nameof(StillIsMissing), nameof(StillPromptTitle), nameof(StillPrompt), nameof(StillDetails), nameof(StillMadeFrom), nameof(StillQueuedText), nameof(StillIsQueued) })
        {
            OnPropertyChanged(name);
        }
        StartEditPromptCommand.NotifyCanExecuteChanged();
        UnqueueStillCommand.NotifyCanExecuteChanged();
        ReplaceStillCommand.NotifyCanExecuteChanged();
    }

    partial void OnStillsStateChanged(StageState value) => RefreshStillPanel();

    partial void OnIsEditingPromptChanged(bool value) => RefreshStillPanel();
}
