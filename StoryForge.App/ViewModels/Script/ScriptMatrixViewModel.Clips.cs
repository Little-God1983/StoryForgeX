using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// A shot's Clip cell: its state and version, or "queued" while Regenerate has it waiting for
/// Make clips. While the clips are made, a cell still to come shows running.
/// </summary>
/// <param name="label">The shot's label now, "S01·2", for screen readers.</param>
public sealed partial class ClipCellViewModel(Action<ClipCellViewModel> select, Func<string> label) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(DisplayState), nameof(StateText), nameof(VersionText), nameof(HasClip), nameof(IsQueued), nameof(AccessibleName))]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private ClipView? _clip;

    /// <summary>The Clips stage as a whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(DisplayState), nameof(StateText), nameof(AccessibleName))]
    private StageState _stageState;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasClip => Clip is not null;

    /// <summary>It waits to be made again; while the clips are made it shows running instead.</summary>
    public bool IsQueued => Clip is { Queued: true, State: not StageState.Running };

    /// <summary>A run is to make it: it waits, or was never made (its cell has no version).</summary>
    public bool IsWaiting => Clip is null || Clip.Queued || Clip.Clip is null;

    // A shot the run never got to stays "—" when the run failed: the bar says where it failed.
    public StageState State => Clip?.State ?? (StageState == StageState.Running ? StageState.Running : StageState.NotStarted);

    /// <summary>What the cell shows: stale when it is out of date.</summary>
    public StageState DisplayState => ScriptMatrixViewModel.Shown(State, Clip?.Stale);

    public string StateText => ScriptMatrixViewModel.Word(DisplayState);

    /// <summary>"v2", or "queued" while it waits.</summary>
    public string VersionText => IsQueued ? "queued" : Clip is { Version: > 0 } clip ? $"v{clip.Version}" : "";

    [RelayCommand(CanExecute = nameof(HasClip))]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the cell; it follows the cell as it changes.</summary>
    public string AccessibleName => $"{label()} clip: {StateText}{(Clip is { Version: > 0 } clip ? $", version {clip.Version}" : "")}{(IsQueued ? ", queued" : "")}";

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The clips in the matrix (board 2b): each shot's Clip cell, the Clips bar with Approve remaining
/// and Make clips, and the Clip panel: the video, its motion prompt, which model made it,
/// Regenerate, Edit prompt… and Make with… another video profile (the fallback).
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClipsRunning), nameof(IsClipsReview), nameof(IsClipsApproved), nameof(IsClipsFailed), nameof(CanStartClips))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingClipsCommand), nameof(MakeClipsCommand), nameof(CancelClipsCommand),
        nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand), nameof(RerunFromHereCommand))]
    private StageState _clipsState;

    public bool IsClipsRunning => ClipsState == StageState.Running;

    public bool IsClipsReview => ClipsState is StageState.NeedsReview or StageState.Stale;

    public bool IsClipsApproved => ClipsState == StageState.Approved;

    public bool IsClipsFailed => ClipsState == StageState.Failed;

    /// <summary>Every still is approved and the clips were never made: a project from before the Clips stage starts them here.</summary>
    public bool CanStartClips => ClipsState == StageState.NotStarted && _stillsApproved && HasShots;

    private bool _stillsApproved;

    [ObservableProperty]
    private string? _clipsError;

    /// <summary>What making the clips did; its last line shows in the bar while it runs.</summary>
    public ObservableCollection<ActivityLine> ClipsActivity { get; } = [];

    /// <summary>"S01·2: sent to ComfyUI".</summary>
    [ObservableProperty]
    private string _clipsProgress = "";

    /// <summary>"6 of 10 shots approved."</summary>
    [ObservableProperty]
    private string _clipsApprovedSummary = "";

    /// <summary>"2 clips wait to be made again, 1 failed: Make clips makes them."; empty when nothing waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueuedClips))]
    private string _clipsQueuedSummary = "";

    public bool HasQueuedClips => ClipsQueuedSummary.Length > 0;

    /// <summary>"Minimax H3": what the project makes its clips with.</summary>
    [ObservableProperty]
    private string _clipsModel = "";

    /// <summary>The other video profiles a clip can be made with instead (the fallback).</summary>
    public ObservableCollection<ClipModel> ClipModels { get; } = [];

    private bool HasWaitingClips => ClipsState != StageState.NotStarted && ClipCells.Any(c => c.IsWaiting);

    private IEnumerable<ClipCellViewModel> ClipCells => Rows.SelectMany(r => r.Shots).Select(s => s.Clip);

    private bool CanApproveRemainingClips() => IsClipsReview && ClipCells.Any(c => c.Clip?.Clip is not null) && ClipCells.All(c => c.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemainingClips))]
    private Task ApproveRemainingClipsAsync() => ActAsync("approve the clips", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.Clips));

    // The waiting clips run when nothing of the clips runs; a project whose clips were never made starts them.
    private bool CanMakeClips() =>
        CanStartClips || (_stillsApproved && !IsClipsRunning && (HasWaitingClips || IsClipsFailed) && ClipCells.All(c => c.State != StageState.Running));

    /// <summary>Make clips: one run makes the clips waiting and those of shots with none yet, each model's together.</summary>
    [RelayCommand(CanExecute = nameof(CanMakeClips))]
    private Task MakeClipsAsync() => ActAsync("make the clips", () => client.MakeClipsAsync(ProjectId));

    [RelayCommand(CanExecute = nameof(IsClipsRunning))]
    private Task CancelClipsAsync() => ActAsync("cancel the clips", () => client.CancelAsync(ProjectId, PipelineStage.Clips));

    private async Task ReceiveClipsAsync(StageUpdate update)
    {
        if (update.Key is null && update.State == StageState.Running)
        {
            var starting = ClipsState != StageState.Running;
            if (starting)
            {
                _loads++;   // a load still on its way would show the state from before this run
                ClipsActivity.Clear();
                ClipsProgress = "";
                ClipsState = StageState.Running;
                ClipsError = null;
                foreach (var cell in ClipCells)
                {
                    // The ones this run makes (waiting, or with none yet) show their new state as each is done.
                    cell.StageState = StageState.Running;
                    cell.Clip = cell.Clip is { } made && cell.IsWaiting ? made with { State = StageState.Running } : cell.Clip;
                }
                RefreshSelection(keepLive: false);
            }
            if (update.Activity is { } line)
            {
                ClipsActivity.Add(line);
                ClipsProgress = line.Text;
            }
            if (starting)
            {
                await LoadAsync();
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } clipLine)
        {
            if (IsClipPanel && update.Key == SelectedShot?.Key)
            {
                SelectedActivity.Add(clipLine);
            }
            return;
        }
        await LoadAsync();
    }

    private void ApplyClips(ClipsView clips)
    {
        var keepLive = ClipsState == StageState.Running && clips.State == StageState.Running && clips.Activity.Count < ClipsActivity.Count;
        _stillsApproved = clips.StillsApproved;
        ClipsState = clips.State;
        ClipsError = clips.Error;
        ClipsModel = clips.Model;
        if (!keepLive)
        {
            ClipsActivity.Clear();
            foreach (var line in clips.Activity)
            {
                ClipsActivity.Add(line);
            }
            ClipsProgress = clips.Activity.Count > 0 ? clips.Activity[^1].Text : "";
        }
        if (!ClipModels.SequenceEqual(clips.Others))
        {
            ClipModels.Clear();
            foreach (var model in clips.Others)
            {
                ClipModels.Add(model);
            }
        }
        foreach (var shot in Rows.SelectMany(r => r.Shots))
        {
            shot.Clip.StageState = clips.State;
            shot.Clip.Clip = clips.Clips.FirstOrDefault(c => c.Key == shot.Key);
        }
        foreach (var row in Rows)
        {
            row.ShotsChanged();
        }
        var shots = Rows.Sum(r => r.Shots.Count);
        ClipsApprovedSummary = $"{ClipCells.Count(c => c.State == StageState.Approved)} of {shots} shots approved.";
        var queued = ClipCells.Count(c => c.IsQueued);
        var failed = ClipCells.Count(c => c.State == StageState.Failed && !c.IsQueued);
        var missing = clips.State == StageState.NotStarted ? 0 : ClipCells.Count(c => c.Clip?.Clip is null && c.State is not (StageState.Running or StageState.Failed));
        var parts = new List<string>();
        if (queued > 0)
        {
            parts.Add($"{queued} {(queued == 1 ? "clip waits" : "clips wait")} to be made again");
        }
        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }
        if (missing > 0)
        {
            parts.Add($"{missing} {(missing == 1 ? "shot has" : "shots have")} no clip yet");
        }
        ClipsQueuedSummary = parts.Count == 0 ? "" : $"{string.Join(", ", parts)}: Make clips makes {(queued + failed + missing == 1 ? "it" : "them")}.";
        OnPropertyChanged(nameof(CanStartClips));
        ApproveRemainingClipsCommand.NotifyCanExecuteChanged();
        MakeClipsCommand.NotifyCanExecuteChanged();
    }

    private void SelectClip(ClipCellViewModel cell)
    {
        var shot = Rows.SelectMany(r => r.Shots).FirstOrDefault(s => s.Clip == cell);
        Select(Rows.FirstOrDefault(r => shot is not null && r.Shots.Contains(shot)), PipelineStage.Clips, shot);
    }

    // ── The Clip panel ──

    /// <summary>The selected clip, when the panel shows a Clip cell that has one.</summary>
    private ClipView? SelectedClip => IsClipPanel ? SelectedShot?.Clip.Clip : null;

    /// <summary>The video to play; null before it was made, or when the file is gone.</summary>
    public string? ClipVideo => SelectedClip?.Path;

    /// <summary>The clip was made but its file is gone from the projects folder.</summary>
    public bool ClipIsMissing => SelectedClip is { Clip: not null, Path: null };

    /// <summary>"Motion prompt (from the storyboard + the video profile)", or "Your prompt".</summary>
    public string ClipPromptTitle => SelectedClip?.Clip is { YourPrompt: true } ? "Your prompt" : "Motion prompt (from the storyboard + the video profile)";

    public string ClipPrompt => SelectedClip?.Clip?.Prompt ?? "";

    /// <summary>"Made by Minimax H3 · from still v2".</summary>
    public string ClipMadeBy => SelectedClip?.Clip is { } clip ? $"Made by {clip.Model} · from still v{clip.StillVersion}" : "";

    /// <summary>"5.95 s with 0.5 s handles · 1280×704 · seed 184223 · 24 fps".</summary>
    public string ClipDetails => SelectedClip?.Clip is { } clip
        ? string.Create(CultureInfo.InvariantCulture,
            $"{Seconds(clip.Seconds)} s{(clip.Handle > 0 ? $" with {Seconds(clip.Handle)} s handles" : "")} · {clip.Size.Width}×{clip.Size.Height} · seed {clip.Seed}{(clip.Workflow.Length > 0 ? $" · wf {clip.Workflow}" : "")}")
        : "";

    /// <summary>"Waits to be made again with LTX 2.5: Make clips makes it."; empty when it is not queued.</summary>
    public string ClipQueuedText => SelectedClip is { Queued: true } queued
        ? $"Waits to be made again{(queued.QueuedProfile is { } profile ? $" with {ClipModels.FirstOrDefault(m => m.Profile.ProfileId == profile.ProfileId)?.Name ?? "another video profile"}" : "")}"
          + $"{(queued.QueuedPrompt is null ? "" : " from your prompt")}: Make clips makes it."
        : "";

    public bool ClipIsQueued => SelectedClip?.Queued == true;

    /// <summary>Another video profile can be picked for this clip.</summary>
    public bool HasClipModels => ClipModels.Count > 0;

    /// <summary>The video profile picked for Make with….</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeWithCommand))]
    private ClipModel? _selectedClipModel;

    private bool CanEditClip() => IsClipPanel && SegmentIdle() && !IsEditingPrompt;

    private bool CanMakeWith() => CanEditClip() && SelectedClipModel is not null;

    /// <summary>Make with…: the clip waits to be made again with the video profile picked (e.g. LTX 2.5); nothing switches on its own.</summary>
    [RelayCommand(CanExecute = nameof(CanMakeWith))]
    private Task MakeWithAsync()
    {
        var (key, profile) = (SelectedShot!.Key, SelectedClipModel!.Profile);
        return ActAsync("queue the clip", () => client.QueueClipAsync(ProjectId, key, profile: profile));
    }

    [RelayCommand(CanExecute = nameof(CanEditClip))]
    private Task UnqueueClipAsync()
    {
        var key = SelectedShot!.Key;
        return ActAsync("take the clip out of the waiting ones", () => client.UnqueueClipAsync(ProjectId, key));
    }

    private void RefreshClipPanel()
    {
        foreach (var name in new[] { nameof(ClipVideo), nameof(ClipIsMissing), nameof(ClipPromptTitle), nameof(ClipPrompt), nameof(ClipMadeBy), nameof(ClipDetails), nameof(ClipQueuedText), nameof(ClipIsQueued), nameof(HasClipModels) })
        {
            OnPropertyChanged(name);
        }
        MakeWithCommand.NotifyCanExecuteChanged();
        UnqueueClipCommand.NotifyCanExecuteChanged();
        StartEditPromptCommand.NotifyCanExecuteChanged();
    }

    partial void OnClipsStateChanged(StageState value) => RefreshClipPanel();
}
