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

/// <summary>
/// A segment's Voice cell: its audio's state and length. While the whole voice is spoken, each cell
/// shows running until its segment is done.
/// </summary>
public sealed partial class VoiceCellViewModel(string id, Action<VoiceCellViewModel> select) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StateText), nameof(LengthText), nameof(HasVoice), nameof(AccessibleName))]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private VoiceSegmentView? _segment;

    /// <summary>The Voice stage as a whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StateText), nameof(AccessibleName))]
    private StageState _stageState;

    [ObservableProperty]
    private bool _isSelected;

    public string Id => id;

    public bool HasVoice => Segment is not null;

    // A segment the run never got to stays "—" when the run failed: the bar says where it failed.
    public StageState State => Segment?.State ?? (StageState == StageState.Running ? StageState.Running : StageState.NotStarted);

    public string StateText => ScriptMatrixViewModel.Word(State);

    /// <summary>"0:31": how long the audio is.</summary>
    public string LengthText => Segment is null ? "" : ScriptMatrixViewModel.Clock(Segment.Seconds);

    [RelayCommand(CanExecute = nameof(HasVoice))]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the cell; it follows the cell as it changes.</summary>
    public string AccessibleName => HasVoice ? $"{Id} voice: {StateText}, {LengthText}" : $"{Id} voice: {StateText}";

    public override string ToString() => AccessibleName;
}

/// <summary>One row of the matrix: a segment, with its Script cell and its Voice cell.</summary>
public sealed partial class SegmentRowViewModel(SegmentView segment, Action<SegmentRowViewModel> select, Action<VoiceCellViewModel> selectVoice) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Title), nameof(State), nameof(StateText), nameof(VersionText), nameof(AccessibleName))]
    private SegmentView _segment = segment;

    /// <summary>The Script cell is the one selected.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public VoiceCellViewModel Voice { get; } = new(segment.Id, selectVoice);

    public string Id => Segment.Id;

    public string Title => Segment.Title;

    public StageState State => Segment.State;

    /// <summary>"review", "approved", "running" …: the cell's word, as in the canvas legend.</summary>
    public string StateText => ScriptMatrixViewModel.Word(Segment.State);

    public string VersionText => $"v{Segment.Version}";

    [RelayCommand]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the Script cell; it follows the cell as it changes.</summary>
    public string AccessibleName => $"{Id} script: {StateText}, version {Segment.Version}";

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The result matrix: the script's segments as rows with their Script and Voice cells, the selected
/// cell in the panel (a segment's narration, facts and Edit text; a voice's audio, its words lit as
/// they are heard), Approve / Regenerate / versions for either, the bars of both stages, and the log
/// while a stage runs.
/// </summary>
public sealed partial class ScriptMatrixViewModel(IStoryForgeClient client, Guid projectId, IAudioPlayer? player = null) : ObservableObject
{
    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;
    private IReadOnlyDictionary<string, Fact> _facts = new Dictionary<string, Fact>();

    // The voice the player shows: another version or file is shown afresh, the same one keeps playing.
    private string? _shownClip;

    public Guid ProjectId { get; } = projectId;

    /// <summary>Plays the selected voice and lights its words.</summary>
    public VoicePlaybackViewModel Playback { get; } = new(player ?? new NoAudio());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(IsNotStarted), nameof(ShowsLog), nameof(HasSegments))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingCommand), nameof(RegenerateScriptCommand), nameof(CancelCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand), nameof(StartEditCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    public bool IsNotStarted => State == StageState.NotStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVoiceRunning), nameof(IsVoiceReview), nameof(IsVoiceApproved), nameof(IsVoiceFailed))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingVoiceCommand), nameof(RegenerateVoiceCommand), nameof(CancelVoiceCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private StageState _voiceState;

    public bool IsVoiceRunning => VoiceState == StageState.Running;

    public bool IsVoiceReview => VoiceState is StageState.NeedsReview or StageState.Stale;

    public bool IsVoiceApproved => VoiceState == StageState.Approved;

    public bool IsVoiceFailed => VoiceState == StageState.Failed;

    [ObservableProperty]
    private string? _voiceError;

    /// <summary>What the voice run did; its last line shows in the bar while it runs.</summary>
    public ObservableCollection<ActivityLine> VoiceActivity { get; } = [];

    /// <summary>"S03 part 1 of 2: sent to ComfyUI".</summary>
    [ObservableProperty]
    private string _voiceProgress = "";

    /// <summary>"3 of 8 segments approved."</summary>
    [ObservableProperty]
    private string _voiceApprovedSummary = "";

    /// <summary>"voice total 3:52".</summary>
    [ObservableProperty]
    private string _voiceTotal = "";

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

    /// <summary>"script about 3:52 · voice total 3:49 · target 4:00".</summary>
    [ObservableProperty]
    private string _lengthSummary = "";

    /// <summary>Why the script is longer than the target; empty when it fits.</summary>
    [ObservableProperty]
    private string _lengthNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SegmentRowViewModel? _selected;

    /// <summary>Which of the selected row's cells the panel shows: Script or Voice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVoicePanel), nameof(IsScriptPanel))]
    private PipelineStage _selectedStage = PipelineStage.Script;

    public bool IsVoicePanel => SelectedStage == PipelineStage.Voice;

    public bool IsScriptPanel => !IsVoicePanel;

    public bool HasSelection => Selected is not null;

    /// <summary>The selected voice, when the panel shows a Voice cell that has one.</summary>
    private VoiceSegmentView? SelectedVoice => IsVoicePanel ? Selected?.Voice.Segment : null;

    /// <summary>"S01 — Script", "S01 — Voice".</summary>
    public string SelectedHeading => Selected is null ? "" : $"{Selected.Id} — {(IsVoicePanel ? "Voice" : "Script")}";

    /// <summary>"review · version 2 · about 0:48"; for a voice "approved · version 1 · 0:31 · 2 parts".</summary>
    public string SelectedStatus => Selected is null
        ? ""
        : IsVoicePanel
            ? SelectedVoice is { } voice
                ? $"{Word(voice.State)} · version {voice.Version} · {Clock(voice.Seconds)}{(voice.Parts.Count > 1 ? $" · {voice.Parts.Count} parts" : "")}"
                : Selected.Voice.StateText
            : $"{Selected.StateText} · version {Selected.Segment.Version} · about {Clock(Selected.Segment.Seconds)}";

    public string SelectedNarration => Selected?.Segment.Narration ?? "";

    public string? SelectedError => IsVoicePanel ? SelectedVoice?.Error : Selected?.Segment.Error;

    /// <summary>
    /// The selected cell is being made again on its own (written or spoken). A Voice cell still to
    /// come while the whole voice is spoken is not: the stage's bar has the Cancel and the log.
    /// </summary>
    public bool SelectedIsRunning => SelectedState == StageState.Running && !(IsVoicePanel && IsVoiceRunning);

    /// <summary>Its log shows while it is made again, and after that failed.</summary>
    public bool SelectedShowsLog => SelectedIsRunning || (SelectedState == StageState.Failed && SelectedActivity.Count > 0);

    private StageState? SelectedState => IsVoicePanel ? SelectedVoice?.State : Selected?.State;

    /// <summary>What making the selected cell again did.</summary>
    public ObservableCollection<ActivityLine> SelectedActivity { get; } = [];

    public IReadOnlyList<UsedFact> SelectedFacts => Selected is null || IsVoicePanel
        ? []
        : [.. Selected.Segment.FactIds.Select(id => _facts.TryGetValue(id, out var fact) ? new UsedFact(id, fact.Weight, fact.Statement) : new UsedFact(id, 0, "(not on the fact sheet)"))];

    public IReadOnlyList<SegmentVersionChoice> SelectedVersions => IsVoicePanel
        ? SelectedVoice is { } voice
            ? [.. voice.Versions.Select(v => new SegmentVersionChoice(v.Version, v.Version == voice.Version, v.Version == voice.ApprovedVersion))]
            : []
        : Selected is null
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
            var voice = await client.GetVoiceAsync(ProjectId);
            if (mine != _loads)
            {
                return;   // a newer load or a live update came in meanwhile; this view is old
            }
            Apply(view, voice);
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
        if (update.ProjectId != ProjectId || update.Stage is not (PipelineStage.Script or PipelineStage.Voice))
        {
            return;
        }
        if (update.Stage == PipelineStage.Voice)
        {
            await ReceiveVoiceAsync(update);
            return;
        }
        if (update.Key is null && update.State == StageState.Running)
        {
            if (State != StageState.Running)
            {
                _loads++;   // a load still on its way would show the state from before this run
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
            if (update.Key == Selected?.Id && IsScriptPanel)
            {
                SelectedActivity.Add(segmentLine);
            }
            return;
        }
        await LoadAsync();
    }

    private async Task ReceiveVoiceAsync(StageUpdate update)
    {
        if (update.Key is null && update.State == StageState.Running)
        {
            // Only the start of the run: a log line is no reason to drop a load on its way, which
            // may bring a segment just spoken.
            if (VoiceState != StageState.Running)
            {
                _loads++;   // a load still on its way would show the state from before this run
                VoiceActivity.Clear();
                VoiceProgress = "";
                VoiceState = StageState.Running;
                VoiceError = null;
                foreach (var row in Rows)
                {
                    // Every segment is spoken again; each shows its new state as it is done.
                    row.Voice.StageState = StageState.Running;
                    row.Voice.Segment = row.Voice.Segment is { } spoken ? spoken with { State = StageState.Running } : null;
                }
                RefreshSelection(keepLive: false);
            }
            if (update.Activity is { } line)
            {
                VoiceActivity.Add(line);
                VoiceProgress = line.Text;
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } segmentLine)
        {
            if (update.Key == Selected?.Id && IsVoicePanel)
            {
                SelectedActivity.Add(segmentLine);
            }
            return;
        }
        await LoadAsync();
    }

    private void Apply(ScriptView view, VoiceView voice)
    {
        _facts = view.Facts.ToDictionary(f => f.Id);
        State = view.State;
        Error = view.Error;
        Activity.Clear();
        foreach (var line in view.Activity)
        {
            Activity.Add(line);
        }

        var shown = (Selected?.Id, SelectedStage);
        var wasRunning = SelectedState == StageState.Running;

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
                Rows.Insert(i, new SegmentRowViewModel(view.Segments[i], Select, SelectVoice));
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
        ApplyVoice(voice);
        if (Selected is null || !Rows.Contains(Selected))
        {
            Select(Rows.FirstOrDefault(), PipelineStage.Script);
        }
        RefreshSelection(keepLive: wasRunning && (Selected?.Id, SelectedStage) == shown);

        var approved = view.Segments.Count(s => s.State == StageState.Approved);
        ApprovedSummary = $"{approved} of {view.Segments.Count} segments approved.";
        VoiceTotal = voice.Segments.Count == 0 ? "" : $"voice total {Clock(voice.Seconds)}";
        LengthSummary = view.Segments.Count == 0
            ? ""
            : string.Join(" · ", new[] { $"script about {Clock(view.Seconds)}", VoiceTotal, $"target {Clock(view.TargetSeconds)}" }.Where(s => s.Length > 0));
        LengthNote = view.LengthNote;
        OnPropertyChanged(nameof(ShowsLog));
        OnPropertyChanged(nameof(HasSegments));
        ApproveRemainingCommand.NotifyCanExecuteChanged();
        ApproveRemainingVoiceCommand.NotifyCanExecuteChanged();
        RegenerateVoiceCommand.NotifyCanExecuteChanged();
    }

    private void ApplyVoice(VoiceView voice)
    {
        // A load taken before the latest lines of a run still going is older than they are: they stay.
        var keepLive = VoiceState == StageState.Running && voice.State == StageState.Running && voice.Activity.Count < VoiceActivity.Count;
        VoiceState = voice.State;
        VoiceError = voice.Error;
        if (!keepLive)
        {
            VoiceActivity.Clear();
            foreach (var line in voice.Activity)
            {
                VoiceActivity.Add(line);
            }
            VoiceProgress = voice.Activity.Count > 0 ? voice.Activity[^1].Text : "";
        }
        foreach (var row in Rows)
        {
            row.Voice.StageState = voice.State;
            row.Voice.Segment = voice.Segments.FirstOrDefault(s => s.Id == row.Id);
        }
        var approved = voice.Segments.Count(s => s.State == StageState.Approved);
        VoiceApprovedSummary = $"{approved} of {voice.Segments.Count} segments approved.";
    }

    private void Select(SegmentRowViewModel? row) => Select(row, PipelineStage.Script);

    private void SelectVoice(VoiceCellViewModel cell) => Select(Rows.FirstOrDefault(r => r.Voice == cell), PipelineStage.Voice);

    private void Select(SegmentRowViewModel? row, PipelineStage stage)
    {
        if (row is not null && row == Selected && stage == SelectedStage)
        {
            return;   // already shown: reading it again would swap live log lines for an older load
        }
        if (Selected is not null)
        {
            Selected.IsSelected = false;
            Selected.Voice.IsSelected = false;
        }
        var sameRow = Selected == row;
        SelectedStage = stage;
        Selected = row;
        if (row is not null)
        {
            row.IsSelected = stage == PipelineStage.Script;
            row.Voice.IsSelected = stage == PipelineStage.Voice;
        }
        IsEditing = false;
        if (sameRow)
        {
            RefreshSelection(keepLive: false);   // the same row, another cell: the panel changes all the same
        }
        else
        {
            RefreshPanel();
        }
    }

    /// <summary>The selected row's cell changed in place: the panel reads it again.</summary>
    private void RefreshSelection(bool keepLive)
    {
        ShowSelectedActivity(keepLive);
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        foreach (var name in new[] { nameof(SelectedHeading), nameof(SelectedStatus), nameof(SelectedNarration), nameof(SelectedFacts), nameof(SelectedVersions), nameof(SelectedError), nameof(SelectedIsRunning), nameof(SelectedShowsLog) })
        {
            OnPropertyChanged(name);
        }
        ApproveSegmentCommand.NotifyCanExecuteChanged();
        RegenerateSegmentCommand.NotifyCanExecuteChanged();
        StartEditCommand.NotifyCanExecuteChanged();
        CancelSegmentCommand.NotifyCanExecuteChanged();
        ShowClip();
    }

    /// <summary>The player follows the selection: a voice that changed is shown afresh, anything else stops it.</summary>
    private void ShowClip()
    {
        var voice = SelectedVoice;
        var clip = voice is null ? null : $"{voice.Id}|{voice.Version}|{voice.AudioPath}";
        if (clip == _shownClip)
        {
            return;
        }
        _shownClip = clip;
        Playback.Show(voice);
    }

    partial void OnSelectedChanged(SegmentRowViewModel? value) => ShowSelectedActivity(keepLive: false);

    /// <param name="keepLive">
    /// The cell ran before this load and runs still: a load with fewer lines than came live since
    /// is older than they are, and they stay.
    /// </param>
    private void ShowSelectedActivity(bool keepLive)
    {
        var lines = (IsVoicePanel ? SelectedVoice?.Activity : Selected?.Segment.Activity) ?? [];
        if (keepLive && SelectedState == StageState.Running && lines.Count < SelectedActivity.Count)
        {
            return;
        }
        SelectedActivity.Clear();
        foreach (var line in lines)
        {
            SelectedActivity.Add(line);
        }
    }

    /// <summary>The selected cell can be acted on: it exists, and neither it nor its whole stage is running.</summary>
    private bool SegmentIdle() => IsVoicePanel
        ? SelectedVoice is { State: not StageState.Running } && !IsVoiceRunning
        : Selected is not null && !IsRunning && Selected.State != StageState.Running;

    private bool CanApproveSegment() => SegmentIdle() && SelectedState != StageState.Approved;

    /// <summary>The selected cell's version shown now.</summary>
    private int SelectedVersion => IsVoicePanel ? SelectedVoice!.Version : Selected!.Segment.Version;

    [RelayCommand(CanExecute = nameof(CanApproveSegment))]
    private Task ApproveSegmentAsync() =>
        ActAsync(IsVoicePanel ? "approve the voice" : "approve the segment", () => client.ApproveSegmentAsync(ProjectId, SelectedStage, Selected!.Id, SelectedVersion));

    // With every segment approved and the stage still in review, it settles the stage.
    private bool CanApproveRemaining() => IsReview && Rows.Count > 0 && Rows.All(r => r.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemaining))]
    private Task ApproveRemainingAsync() => ActAsync("approve the script", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.Script));

    [RelayCommand(CanExecute = nameof(SegmentIdle))]
    private Task RegenerateSegmentAsync() =>
        ActAsync(IsVoicePanel ? "speak the segment again" : "write the segment again", () => client.RegenerateSegmentAsync(ProjectId, SelectedStage, Selected!.Id));

    private bool CanRegenerateScript() => !IsRunning;

    /// <summary>Regenerate the whole script; Retry after a failure; Write the script before the first run.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateScript))]
    private Task RegenerateScriptAsync() => ActAsync("write the script", () => client.RegenerateAsync(ProjectId, PipelineStage.Script));

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task CancelAsync() => ActAsync("cancel the script", () => client.CancelAsync(ProjectId, PipelineStage.Script));

    [RelayCommand(CanExecute = nameof(SelectedIsRunning))]
    private Task CancelSegmentAsync() => ActAsync("cancel the segment", () => client.CancelSegmentAsync(ProjectId, SelectedStage, Selected!.Id));

    [RelayCommand]
    private Task ShowVersionAsync(int version) =>
        ActAsync("switch the version", () => client.SelectSegmentVersionAsync(ProjectId, SelectedStage, Selected!.Id, version));

    private bool CanApproveRemainingVoice() => IsVoiceReview && Rows.Any(r => r.Voice.HasVoice) && Rows.All(r => r.Voice.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemainingVoice))]
    private Task ApproveRemainingVoiceAsync() => ActAsync("approve the voice", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.Voice));

    private bool CanRegenerateVoice() => !IsVoiceRunning && IsApproved && Rows.All(r => r.Voice.State != StageState.Running);

    /// <summary>Regenerate the whole voice; Retry after a failure.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateVoice))]
    private Task RegenerateVoiceAsync() => ActAsync("speak the script", () => client.RegenerateAsync(ProjectId, PipelineStage.Voice));

    [RelayCommand(CanExecute = nameof(IsVoiceRunning))]
    private Task CancelVoiceAsync() => ActAsync("cancel the voice", () => client.CancelAsync(ProjectId, PipelineStage.Voice));

    private bool CanStartEdit() => IsScriptPanel && SegmentIdle();

    [RelayCommand(CanExecute = nameof(CanStartEdit))]
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

    internal static string Clock(double seconds)
    {
        var whole = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    /// <summary>"review", "approved", "running" …: a cell's word, as in the canvas legend.</summary>
    internal static string Word(StageState state) => state switch
    {
        StageState.NeedsReview => "review",
        StageState.NotStarted => "—",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>No player: tests and previews that never play.</summary>
    private sealed class NoAudio : IAudioPlayer
    {
        public TimeSpan Position { get; set; }

        public event EventHandler? Ticked { add { } remove { } }

        public event EventHandler? Ended { add { } remove { } }

        public event EventHandler<string>? Failed { add { } remove { } }

        public void Open(string path) => throw new NotSupportedException("Audio cannot be played here.");

        public void Play() { }

        public void Pause() { }

        public void Close() { }
    }
}
