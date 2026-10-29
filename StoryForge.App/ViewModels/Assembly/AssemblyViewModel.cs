using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Assembly;

/// <summary>One shot on the Assembly timeline: as wide as it lasts, with the cut after it marked.</summary>
public sealed partial class CutBlockViewModel(int index, AssemblyShot shot, Action<int> select) : ObservableObject
{
    public int Index { get; } = index;

    public AssemblyShot Shot { get; } = shot;

    /// <summary>Its width on the timeline.</summary>
    public double Seconds => Math.Max(0.1, Shot.End - Shot.Start);

    public string SecondsText => $"{AssemblyViewModel.Number(Seconds)} s";

    /// <summary>"| ", "◇ 0.4 s", "■ 0.6 s", with "· J 0.3" or "· L 0.2"; empty after the last shot.</summary>
    [ObservableProperty]
    private string _mark = "";

    /// <summary>The cut after it is yours: changed from the version shown.</summary>
    [ObservableProperty]
    private bool _isYours;

    [ObservableProperty]
    private bool _isSelected;

    public string AccessibleName => $"{Shot.Label}, {SecondsText}{(Mark.Length > 0 ? $", then {Mark}" : "")}";

    [RelayCommand]
    private void Select() => select(Index);
}

/// <summary>A version of the video: "v1", "v2 · yours".</summary>
public sealed partial class AssemblyVersionViewModel(ResultVersion version, bool shown, bool approved, Func<int, Task> show) : ObservableObject
{
    public int Version => version.Version;

    public string Label => $"v{version.Version}{(version.Origin == VersionOrigin.Edited ? " · yours" : "")}{(approved ? " ✓" : "")}";

    public bool IsShown => shown;

    [RelayCommand]
    private Task Show() => show(version.Version);
}

/// <summary>
/// The Assembly screen (board 10): the finished video and its versions, the shots as blocks on the
/// narration's timeline with each cut marked, and the panel of the selected cut, where you move it,
/// change its transition or its J/L, and render again. Approve, Regenerate (Claude cuts it anew)
/// and Cancel are in the bar.
/// </summary>
public sealed partial class AssemblyViewModel(IStoryForgeClient client, Guid projectId) : ObservableObject
{
    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;
    private AssemblyView? _view;
    private int? _wanted;
    private List<Cut> _draft = [];
    private bool _syncing;

    public Guid ProjectId { get; } = projectId;

    /// <summary>Asks the player to go to a moment: just before the selected cut. The view seeks it.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(IsNotStarted), nameof(BarText))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RegenerateCommand), nameof(RenderAgainCommand), nameof(CancelCommand), nameof(RetryCommand), nameof(MakeVideoCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    public bool IsNotStarted => State == StageState.NotStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarText))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand), nameof(RenderAgainCommand), nameof(MakeVideoCommand))]
    private bool _clipsApproved;

    /// <summary>Why the last run failed, or why your cut list was refused; null when all is well.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Why the video is out of date, e.g. "S02·1's clip changed since the video was cut."</summary>
    [ObservableProperty]
    private string? _stale;

    /// <summary>The last thing the run did, e.g. "rendering 60 shots, 232 s, with FFmpeg".</summary>
    [ObservableProperty]
    private string? _activity;

    /// <summary>The video to play; null before one was made or when its file is gone.</summary>
    [ObservableProperty]
    private string? _videoPath;

    /// <summary>"v2 · your edit of v1 · 3:52 · 1920×1080".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarText))]
    private string _videoInfo = "";

    /// <summary>The version shown; null before the first.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private int? _version;

    public ObservableCollection<AssemblyVersionViewModel> Versions { get; } = [];

    public ObservableCollection<CutBlockViewModel> Blocks { get; } = [];

    /// <summary>"Gate: Assembly", what the bar above says for the stage as it stands.</summary>
    public string BarText => State switch
    {
        StageState.Running => "Cutting the video…",
        StageState.NeedsReview or StageState.Stale => $"{VideoInfo}. Approve it, or edit a cut and render again.",
        StageState.Approved => $"{VideoInfo}. Approved.",
        StageState.Failed => "The video could not be cut.",
        _ => ClipsApproved ? "Every clip is approved: cut the video." : "The video is cut once every clip is approved.",
    };

    // ── The selected cut ──

    /// <summary>The cut shown in the panel: the one after shot <see cref="SelectedCut"/>; -1 for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CutHeading), nameof(CutWords), nameof(CutWhy), nameof(ShiftText), nameof(SoundText))]
    [NotifyCanExecuteChangedFor(nameof(EarlierCommand), nameof(LaterCommand))]
    private int _selectedCut = -1;

    public bool HasSelection => SelectedCut >= 0 && SelectedCut < _draft.Count && SelectedCut + 1 < Blocks.Count;

    private Cut? Selected => HasSelection ? _draft[SelectedCut] : null;

    /// <summary>"Cut 7 · S02·3 → S03·1".</summary>
    /// <summary>Why the cuts cannot be edited (the shots changed since the video was cut); null when they can.</summary>
    [ObservableProperty]
    private string? _editBlocked;

    public string CutHeading => HasSelection ? $"Cut {SelectedCut + 1} · {Blocks[SelectedCut].Shot.Label} → {Blocks[SelectedCut + 1].Shot.Label}" : "";

    /// <summary>"…holds a soul." → "The Blood War…": the words either side of the cut.</summary>
    public string CutWords => HasSelection ? $"“…{Tail(Blocks[SelectedCut].Shot.Heard)}” → “{Head(Blocks[SelectedCut + 1].Shot.Heard)}…”" : "";

    public string CutWhy => Selected?.Why is { Length: > 0 } why ? why : "—";

    public static IReadOnlyList<CutTransition> Transitions { get; } = [CutTransition.Cut, CutTransition.Crossfade, CutTransition.DipToBlack];

    [ObservableProperty]
    private CutTransition _transition;

    /// <summary>A crossfade's or dip's length; "0" for a hard cut.</summary>
    [ObservableProperty]
    private string _transitionSeconds = "0";

    /// <summary>"0.2 s before the words", "on the words".</summary>
    public string ShiftText => Selected is { } cut ? cut.Shift switch
    {
        < 0 => $"{Number(-cut.Shift)} s before the words",
        > 0 => $"{Number(cut.Shift)} s after the words",
        _ => "on the words",
    } : "";

    /// <summary>The clips' sound change: + before the picture (J cut), - after it (L cut).</summary>
    [ObservableProperty]
    private string _soundLead = "0";

    /// <summary>"J · 0.3 s before the picture: the next clip is heard first".</summary>
    public string SoundText => Selected is { } cut ? cut.SoundLead switch
    {
        > 0 => $"J · {Number(cut.SoundLead)} s before the picture: the next clip is heard first",
        < 0 => $"L · {Number(-cut.SoundLead)} s after the picture: this clip is still heard",
        _ => "with the picture",
    } : "";

    /// <summary>The clips' own sound under the narration, in dB.</summary>
    [ObservableProperty]
    private string _clipSound = "";

    /// <summary>Your edits differ from the version shown: Render again makes them a new version.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderAgainCommand), nameof(UndoCommand), nameof(ApproveCommand))]
    private bool _hasEdits;

    /// <summary>A number in the panel that is not one.</summary>
    [ObservableProperty]
    private string? _editProblem;

    public async Task LoadAsync()
    {
        var load = ++_loads;
        AssemblyView view;
        try
        {
            view = await client.GetAssemblyAsync(ProjectId, _wanted);
        }
        catch (KeyNotFoundException) when (_wanted is not null)
        {
            _wanted = null;   // that version is gone: the current one
            view = await client.GetAssemblyAsync(ProjectId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the Assembly of {ProjectId} failed: {ex}");
            Error = $"Could not load the Assembly: {ex.Message}";
            return;
        }
        if (load == _loads)
        {
            Apply(view);
        }
    }

    public async Task ReceiveAsync(StageUpdate update)
    {
        // A clip's progress lines change nothing here; a clip made or approved does.
        if (update.ProjectId == ProjectId && (update.Stage == PipelineStage.Assembly || (update.Stage == PipelineStage.Clips && update.Activity is null)))
        {
            if (update.Stage == PipelineStage.Assembly && update.Activity is { } line && State == StageState.Running)
            {
                Activity = line.Text;
                return;
            }
            await LoadAsync();
        }
    }

    private void Apply(AssemblyView view)
    {
        var keepEdits = HasEdits && _view?.Version == view.Version && _view?.Shots.Count == view.Shots.Count;
        _view = view;
        State = view.State;
        ClipsApproved = view.ClipsApproved;
        Stale = view.Stale;
        Error = view.Error;
        Activity = view.Activity.LastOrDefault()?.Text;
        VideoPath = view.Path;
        Version = view.Version;
        VideoInfo = view.Video is { } video ? Info(view, video) : "";
        Versions.Clear();
        foreach (var version in view.Versions)
        {
            Versions.Add(new AssemblyVersionViewModel(version, version.Version == view.Version, version.Version == view.ApprovedVersion, ShowVersionAsync));
        }
        var selected = SelectedCut;
        Blocks.Clear();
        for (var i = 0; i < view.Shots.Count; i++)
        {
            Blocks.Add(new CutBlockViewModel(i, view.Shots[i], SelectCut));
        }
        if (!keepEdits)
        {
            // Your cut list while it renders, or after its render failed (to edit again); else the version shown.
            var list = view.State is StageState.Failed or StageState.Running && view.Waiting is { } waiting ? waiting : view.Video?.CutList;
            _draft = [.. list?.Cuts ?? []];
            _syncing = true;
            ClipSound = Number(list?.ClipSoundDb ?? CutList.DefaultClipSoundDb);
            _syncing = false;
        }
        // Cut for other shots (the storyboard was cut again since): nothing to edit until it is cut again.
        EditBlocked = _draft.Count > 0 && !Fits(_draft, view.Shots) ? "The shots changed since this video was cut: Regenerate cuts it again for them." : null;
        var index = EditBlocked is not null || _draft.Count == 0 ? -1 : Math.Clamp(selected < 0 ? 0 : selected, 0, _draft.Count - 1);
        if (index == SelectedCut)
        {
            OnSelectedCutChanged(index);   // the same place, new blocks and maybe other values: the panel follows
            NotifySelection();
        }
        SelectedCut = index;
        Refresh();
    }

    /// <summary>One cut after every shot but the last, each after its shot: the blocks and the cuts line up.</summary>
    private static bool Fits(IReadOnlyList<Cut> cuts, IReadOnlyList<AssemblyShot> shots) =>
        cuts.Count == shots.Count - 1 && cuts.Select((c, i) => c.After == shots[i].Key).All(x => x);

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        EarlierCommand.NotifyCanExecuteChanged();
        LaterCommand.NotifyCanExecuteChanged();
    }

    private static string Info(AssemblyView view, AssembledVideo video)
    {
        var length = TimeSpan.FromSeconds(Math.Round(video.Seconds));
        var from = video.BasedOn is { } basedOn ? $" · your edit of v{basedOn}" : "";
        return $"v{view.Version}{from} · {(int)length.TotalMinutes}:{length.Seconds:00} · {video.Size.Width}×{video.Size.Height}";
    }

    private Task ShowVersionAsync(int version)
    {
        _wanted = version;
        HasEdits = false;
        return LoadAsync();
    }

    private void SelectCut(int index)
    {
        if (_draft.Count == 0 || EditBlocked is not null)
        {
            return;
        }
        SelectedCut = Math.Min(index, _draft.Count - 1);
        Refresh();
        SeekRequested?.Invoke(this, TimeSpan.FromSeconds(Math.Max(0, Blocks[SelectedCut].Shot.End - 1.5)));
    }

    partial void OnSelectedCutChanged(int value)
    {
        _syncing = true;
        if (Selected is { } cut)
        {
            Transition = cut.Transition;
            TransitionSeconds = Number(cut.Seconds);
            SoundLead = Number(cut.SoundLead);
        }
        _syncing = false;
        foreach (var block in Blocks)
        {
            block.IsSelected = block.Index == value;
        }
    }

    partial void OnTransitionChanged(CutTransition value)
    {
        if (_syncing || Selected is not { } cut)
        {
            return;
        }
        // A hard cut has no length; a crossfade or dip starts at 0.4 s.
        var seconds = value == CutTransition.Cut ? 0 : cut.Seconds > 0 ? cut.Seconds : 0.4;
        Change(cut with { Transition = value, Seconds = seconds });
        _syncing = true;
        TransitionSeconds = Number(seconds);
        _syncing = false;
    }

    partial void OnTransitionSecondsChanged(string value)
    {
        if (!_syncing && Selected is { } cut && Parse(value, "the length") is { } seconds)
        {
            Change(cut with { Seconds = cut.Transition == CutTransition.Cut ? 0 : seconds });
        }
    }

    partial void OnSoundLeadChanged(string value)
    {
        if (!_syncing && Selected is { } cut && Parse(value, "the sound change") is { } lead)
        {
            Change(cut with { SoundLead = lead });
        }
    }

    partial void OnClipSoundChanged(string value)
    {
        if (!_syncing && Parse(value, "the clip sound") is not null)
        {
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Earlier() => Move(-0.1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Later() => Move(0.1);

    private void Move(double by)
    {
        if (Selected is { } cut)
        {
            Change(cut with { Shift = Math.Round(cut.Shift + by, 2) });
        }
    }

    private void Change(Cut cut)
    {
        _draft[SelectedCut] = cut;
        Refresh();
    }

    /// <summary>The marks, the panel and whether anything is yours, from the draft.</summary>
    private void Refresh()
    {
        var shown = _view?.Video?.CutList;
        for (var i = 0; i < Blocks.Count; i++)
        {
            var cut = EditBlocked is null && i < _draft.Count ? _draft[i] : null;
            Blocks[i].Mark = cut is null ? "" : Mark(cut);
            Blocks[i].IsYours = cut is not null && (cut.Yours || (shown is not null && i < shown.Cuts.Count && !shown.Cuts[i].SameAs(cut)));
        }
        var sound = Parse(ClipSound, "the clip sound", report: false);
        HasEdits = (shown is null ? _draft.Count > 0 : _draft.Count != shown.Cuts.Count || _draft.Where((c, i) => !shown.Cuts[i].SameAs(c)).Any())
            || (shown is not null && sound is { } db && Math.Abs(db - shown.ClipSoundDb) > 0.001)
            || (State == StageState.Failed && _view?.Waiting is not null);
        OnPropertyChanged(nameof(ShiftText));
        OnPropertyChanged(nameof(SoundText));
        OnPropertyChanged(nameof(CutHeading));
        OnPropertyChanged(nameof(CutWords));
        OnPropertyChanged(nameof(CutWhy));
    }

    private static string Mark(Cut cut)
    {
        var mark = cut.Transition switch
        {
            CutTransition.Crossfade => $"◇ {Number(cut.Seconds)} s",
            CutTransition.DipToBlack => $"■ {Number(cut.Seconds)} s",
            _ => "|",
        };
        return cut.SoundLead switch
        {
            > 0 => $"{mark} · J {Number(cut.SoundLead)}",
            < 0 => $"{mark} · L {Number(-cut.SoundLead)}",
            _ => mark,
        };
    }

    private double? Parse(string text, string what, bool report = true)
    {
        if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        {
            if (report)
            {
                EditProblem = null;
            }
            return Math.Round(value, 2);
        }
        if (report)
        {
            EditProblem = $"Give {what} as a number, e.g. 0.4.";
        }
        return null;
    }

    // ── The bar ──

    [RelayCommand(CanExecute = nameof(CanRenderAgain))]
    private async Task RenderAgainAsync()
    {
        if (Parse(ClipSound, "the clip sound") is not { } sound)
        {
            return;
        }
        await ActAsync(() => client.RenderCutListAsync(ProjectId, new CutList(_draft, sound, _view?.Video?.CutList.Music ?? []), Version));
    }

    private bool CanRenderAgain() => HasEdits && !IsRunning && ClipsApproved && EditBlocked is null;

    [RelayCommand(CanExecute = nameof(HasEdits))]
    private void Undo()
    {
        HasEdits = false;
        if (_view is { } view)
        {
            Apply(view with { Waiting = null });
        }
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private Task ApproveAsync() => ActAsync(() => client.ApproveAsync(ProjectId, PipelineStage.Assembly, Version!.Value));

    private bool CanApprove() => IsReview && Version is not null && !HasEdits;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync() => ActAsync(() => client.RegenerateAsync(ProjectId, PipelineStage.Assembly));

    private bool CanRegenerate() => !IsRunning && !IsNotStarted && ClipsApproved;

    [RelayCommand(CanExecute = nameof(CanMakeVideo))]
    private Task MakeVideoAsync() => ActAsync(() => client.RegenerateAsync(ProjectId, PipelineStage.Assembly));

    private bool CanMakeVideo() => IsNotStarted && ClipsApproved;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CancelAsync()
    {
        var (draft, sound, version) = (_draft, ClipSound, _view?.Version);
        await ActAsync(() => client.CancelAsync(ProjectId, PipelineStage.Assembly));
        // Your edits stay on screen to render again, as long as the video they were made on is shown and they fit its shots.
        if (Version == version && _view is { } view && Fits(draft, view.Shots))
        {
            _draft = [.. draft];
            _syncing = true;
            ClipSound = sound;
            _syncing = false;
            OnSelectedCutChanged(SelectedCut);
            Refresh();
        }
    }

    /// <summary>Your cut list again when it was yours that failed; else Claude cuts it again.</summary>
    [RelayCommand(CanExecute = nameof(IsFailed))]
    private Task RetryAsync() => _view?.Waiting is { } waiting
        ? ActAsync(() => client.RenderCutListAsync(ProjectId, waiting, _view.WaitingBasedOn))
        : ActAsync(() => client.RegenerateAsync(ProjectId, PipelineStage.Assembly));

    private async Task ActAsync(Func<Task> act)
    {
        try
        {
            Error = null;
            _wanted = null;
            await act();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Error = ex.Message;
            return;
        }
        HasEdits = false;
        await LoadAsync();
    }

    internal static string Number(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Tail(string words) => string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));

    private static string Head(string words) => string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
}
