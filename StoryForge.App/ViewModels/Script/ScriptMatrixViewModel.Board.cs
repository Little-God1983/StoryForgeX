using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>
/// The storyboard in the matrix (board 2b): each segment's shots under it, folding, the
/// storyboard's bars and counts.
/// </summary>
public sealed partial class ScriptMatrixViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoardRunning), nameof(IsBoardReview), nameof(IsBoardApproved), nameof(IsBoardFailed), nameof(CanPlanBoard))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingBoardCommand), nameof(RegenerateBoardCommand), nameof(CancelBoardCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private StageState _boardState;

    public bool IsBoardRunning => BoardState == StageState.Running;

    public bool IsBoardReview => BoardState is StageState.NeedsReview or StageState.Stale;

    public bool IsBoardApproved => BoardState == StageState.Approved;

    public bool IsBoardFailed => BoardState == StageState.Failed;

    /// <summary>
    /// The voice is approved and the storyboard was never planned: a project voiced before the
    /// storyboard stage existed starts it from here.
    /// </summary>
    public bool CanPlanBoard => BoardState == StageState.NotStarted && IsVoiceApproved;

    [ObservableProperty]
    private string? _boardError;

    /// <summary>What planning the storyboard did; its last line shows in the bar while it runs.</summary>
    public ObservableCollection<ActivityLine> BoardActivity { get; } = [];

    /// <summary>"planning the shots".</summary>
    [ObservableProperty]
    private string _boardProgress = "";

    /// <summary>"9 of 14 shots approved."</summary>
    [ObservableProperty]
    private string _boardApprovedSummary = "";

    /// <summary>"Pacing: shots of 3–8 s, about 5 s · 14 shots · shortest 4.2 s · longest 6.1 s · max clip 20 s".</summary>
    [ObservableProperty]
    private string _pacingSummary = "";

    /// <summary>"14 shots → 14 stills, 14 clips"; empty before the storyboard.</summary>
    private string _shotCountText = "";

    /// <summary>The storyboard as last loaded: its words and cast, for the Board panel.</summary>
    private StoryboardView? _board;

    /// <summary>Folding needs shots to fold away.</summary>
    public bool ShowsFoldAll => HasShots;

    /// <summary>"Fold all" while any segment is open, else "Open all".</summary>
    public string FoldAllLabel => Rows.Any(r => r.HasShots && r.IsOpen) ? "Fold all" : "Open all";

    [RelayCommand]
    private void FoldAll()
    {
        var open = !Rows.Any(r => r.HasShots && r.IsOpen);
        foreach (var row in Rows)
        {
            row.IsOpen = open;
        }
    }

    private bool HasShots => Rows.Any(r => r.HasShots);

    private bool CanApproveRemainingBoard() => IsBoardReview && HasShots && Rows.SelectMany(r => r.Shots).All(s => s.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemainingBoard))]
    private Task ApproveRemainingBoardAsync() => ActAsync("approve the storyboard", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.Storyboard));

    private bool CanRegenerateBoard() => !IsBoardRunning && Rows.SelectMany(r => r.Shots).All(s => s.State != StageState.Running);

    /// <summary>Regenerate the whole storyboard; Retry after a failure.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateBoard))]
    private Task RegenerateBoardAsync() => ActAsync("plan the storyboard", () => client.RegenerateAsync(ProjectId, PipelineStage.Storyboard));

    [RelayCommand(CanExecute = nameof(IsBoardRunning))]
    private Task CancelBoardAsync() => ActAsync("cancel the storyboard", () => client.CancelAsync(ProjectId, PipelineStage.Storyboard));

    private async Task ReceiveBoardAsync(StageUpdate update)
    {
        if (update.Key is null && update.State == StageState.Running)
        {
            var starting = BoardState != StageState.Running;
            if (starting)
            {
                _loads++;   // a load still on its way would show the state from before this run
                BoardActivity.Clear();
                BoardProgress = "";
                BoardState = StageState.Running;
                BoardError = null;
            }
            if (update.Activity is { } line)
            {
                BoardActivity.Add(line);
                BoardProgress = line.Text;
            }
            if (starting)
            {
                // ... and what that load was bringing (the voice approved, which started this run) comes with a new one.
                await LoadAsync();
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } shotLine)
        {
            if (IsBoardPanel && update.Key == SelectedShot?.Key)
            {
                SelectedActivity.Add(shotLine);
            }
            return;
        }
        await LoadAsync();
    }

    private void ApplyBoard(StoryboardView board)
    {
        _board = board;
        var keepLive = BoardState == StageState.Running && board.State == StageState.Running && board.Activity.Count < BoardActivity.Count;
        BoardState = board.State;
        BoardError = board.Error;
        if (!keepLive)
        {
            BoardActivity.Clear();
            foreach (var line in board.Activity)
            {
                BoardActivity.Add(line);
            }
            BoardProgress = board.Activity.Count > 0 ? board.Activity[^1].Text : "";
        }

        // Shots that are still there keep their place and selection; the rest come and go.
        foreach (var row in Rows)
        {
            var shots = board.Shots.Where(s => s.SegmentId == row.Id).ToList();
            for (var i = row.Shots.Count - 1; i >= 0; i--)
            {
                if (shots.All(s => s.Key != row.Shots[i].Key))
                {
                    row.Shots.RemoveAt(i);
                }
            }
            for (var i = 0; i < shots.Count; i++)
            {
                var shown = row.Shots.FirstOrDefault(s => s.Key == shots[i].Key);
                if (shown is null)
                {
                    row.Shots.Insert(i, new ShotRowViewModel(shots[i], SelectShot, SelectStill));
                }
                else
                {
                    shown.Shot = shots[i];
                    if (row.Shots.IndexOf(shown) != i)
                    {
                        row.Shots.Move(row.Shots.IndexOf(shown), i);
                    }
                }
            }
            row.ShotsChanged();
        }

        var count = board.Shots.Count;
        BoardApprovedSummary = $"{board.Shots.Count(s => s.State == StageState.Approved)} of {count} shots approved.";
        _shotCountText = count == 0 ? "" : $"{count} shots → {count} stills, {count} clips";
        PacingSummary = count == 0
            ? ""
            : $"Pacing: shots of {board.Pacing} · {count} shots · shortest {Seconds(board.Shots.Min(s => s.Shot.Seconds))} s · longest {Seconds(board.Shots.Max(s => s.Shot.Seconds))} s · max clip {board.MaxClipSeconds} s";
        OnPropertyChanged(nameof(FoldAllLabel));
        OnPropertyChanged(nameof(ShowsFoldAll));
        ApproveRemainingBoardCommand.NotifyCanExecuteChanged();
        RegenerateBoardCommand.NotifyCanExecuteChanged();
    }

    // Running or not, the panel's controls follow: nothing on the storyboard changes while it is planned.
    partial void OnBoardStateChanged(StageState value) => RefreshBoardPanel();

    partial void OnIsEditingShotChanged(bool value)
    {
        RefreshBoardPanel();
        RefreshRowCommands();   // the whole script's Regenerate and Edit text would drop it
    }

    private void SelectShot(ShotRowViewModel shot) => Select(Rows.FirstOrDefault(r => r.Shots.Contains(shot)), PipelineStage.Storyboard, shot);

    /// <summary>"6.1", "20": a shot's length in the panel and the pacing line.</summary>
    internal static string Seconds(double seconds) => Math.Round(seconds, 1).ToString("0.#", CultureInfo.InvariantCulture);

    // ── The Board panel: what board 8 drew, for the selected shot ──

    /// <summary>Saving "continues the shot before" after the tick; tests await it.</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>What the shot shows.</summary>
    public string BoardShows => SelectedShot?.Description ?? "";

    /// <summary>The segment's words, the shot's lit; a word inside the shot can be where it splits.</summary>
    public IReadOnlyList<BoardWord> BoardWords => SelectedShot?.Shot.Shot is { } shot
        ? [.. SelectedWords.Select((w, i) => new BoardWord(w.Text, i, i >= shot.FirstWord && i < shot.FirstWord + shot.WordCount, CanSplitAt(i)))]
        : [];

    /// <summary>"w0": the word the shot starts at.</summary>
    public string BoardStartWord => WordAt(SelectedShot?.Shot.Shot.FirstWord);

    /// <summary>"w3": the word it ends after.</summary>
    public string BoardEndWord => WordAt(SelectedShot?.Shot.Shot is { } shot ? shot.FirstWord + shot.WordCount - 1 : null);

    /// <summary>Who and what is in it.</summary>
    public IReadOnlyList<string> BoardCast => SelectedShot?.Shot.Shot.Cast ?? [];

    /// <summary>The cast it can still take: everyone on the cast list not in it yet.</summary>
    public IReadOnlyList<string> BoardCastChoices => [.. (_board?.Cast ?? []).Select(c => c.Name).Where(n => !BoardCast.Contains(n))];

    /// <summary>"Continues the shot before": ticking it saves the shot's next version.</summary>
    public bool BoardContinues
    {
        get => SelectedShot?.Shot.Shot.ContinuesPrevious ?? false;
        set
        {
            if (SelectedShot is null || value == BoardContinues)
            {
                return;
            }
            Saving = SaveContinuesAsync(SelectedShot.Key, value);
        }
    }

    /// <summary>Saves the tick; whatever came of it, the box then shows what the shot has.</summary>
    private async Task SaveContinuesAsync(string key, bool value)
    {
        if (CanEditShot())
        {
            await ActAsync("save the shot", () => client.EditShotAsync(ProjectId, key, new ShotChange(ContinuesPrevious: value)));
        }
        else
        {
            await Task.Yield();   // after the box's own update, or it would not listen
        }
        OnPropertyChanged(nameof(BoardContinues));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditingShot), nameof(IsNotEditingAny))]
    [NotifyCanExecuteChangedFor(nameof(SaveShotDescriptionCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private bool _isEditingShot;

    public bool IsNotEditingShot => !IsEditingShot;

    /// <summary>Neither the script's text, a shot's description nor a still's prompt is being edited: the panel's own buttons show.</summary>
    public bool IsNotEditingAny => !IsEditing && !IsEditingShot && !IsEditingPrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveShotDescriptionCommand))]
    private string _editShotDescription = "";

    // The engine refuses every change while any shot is planned again: a cut touches two shots.
    private bool CanEditShot() => IsBoardPanel && SegmentIdle() && Rows.SelectMany(r => r.Shots).All(s => s.State != StageState.Running);

    /// <summary>The selected shot can be changed now; the panel's controls follow it.</summary>
    public bool CanEditBoard => CanEditShot();

    [RelayCommand(CanExecute = nameof(CanEditShot))]
    private void StartEditShot()
    {
        EditShotDescription = BoardShows;
        IsEditingShot = true;
    }

    private bool CanSaveShotDescription() => IsEditingShot && EditShotDescription.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveShotDescription))]
    private async Task SaveShotDescriptionAsync()
    {
        var key = SelectedShot!.Key;
        await ActAsync("save the shot", () => client.EditShotAsync(ProjectId, key, new ShotChange(Description: EditShotDescription.Trim())));
        if (ActionError is null)
        {
            IsEditingShot = false;   // saved; when it was not, your text stays to try again
        }
    }

    [RelayCommand]
    private void CancelEditShot() => IsEditingShot = false;

    private bool CanAddCast(string? name) => CanEditShot() && !string.IsNullOrWhiteSpace(name) && BoardCast.Count < Shot.MaxCast && !BoardCast.Contains(name);

    [RelayCommand(CanExecute = nameof(CanAddCast))]
    private Task AddCastAsync(string? name)
    {
        var key = SelectedShot!.Key;
        IReadOnlyList<string> cast = [.. BoardCast, name!.Trim()];
        return ActAsync("save the shot", () => client.EditShotAsync(ProjectId, key, new ShotChange(Cast: cast)));
    }

    private bool CanRemoveCast(string? name) => CanEditShot() && name is not null && BoardCast.Contains(name);

    [RelayCommand(CanExecute = nameof(CanRemoveCast))]
    private Task RemoveCastAsync(string? name)
    {
        var key = SelectedShot!.Key;
        IReadOnlyList<string> cast = [.. BoardCast.Where(n => n != name)];
        return ActAsync("save the shot", () => client.EditShotAsync(ProjectId, key, new ShotChange(Cast: cast)));
    }

    private bool CanStartEarlier() => CanMove(ShotEdge.Start, -1);

    private bool CanStartLater() => CanMove(ShotEdge.Start, 1);

    private bool CanEndEarlier() => CanMove(ShotEdge.End, -1);

    private bool CanEndLater() => CanMove(ShotEdge.End, 1);

    [RelayCommand(CanExecute = nameof(CanStartEarlier))]
    private Task StartEarlierAsync() => MoveAsync(ShotEdge.Start, -1);

    [RelayCommand(CanExecute = nameof(CanStartLater))]
    private Task StartLaterAsync() => MoveAsync(ShotEdge.Start, 1);

    [RelayCommand(CanExecute = nameof(CanEndEarlier))]
    private Task EndEarlierAsync() => MoveAsync(ShotEdge.End, -1);

    [RelayCommand(CanExecute = nameof(CanEndLater))]
    private Task EndLaterAsync() => MoveAsync(ShotEdge.End, 1);

    private Task MoveAsync(ShotEdge edge, int words)
    {
        var key = SelectedShot!.Key;
        return ActAsync("move the cut", () => client.MoveCutAsync(ProjectId, key, edge, words));
    }

    [RelayCommand(CanExecute = nameof(CanSplitAt))]
    private Task SplitAtAsync(int index)
    {
        var key = SelectedShot!.Key;
        return ActAsync("split the shot", () => client.SplitShotAsync(ProjectId, key, index));
    }

    private bool CanMergeWithNext()
    {
        var (shots, i) = SegmentShots();
        return CanEditShot() && i >= 0 && i < shots.Count - 1 && Fits(Span(shots[i].FirstWord, shots[i].WordCount + shots[i + 1].WordCount));
    }

    [RelayCommand(CanExecute = nameof(CanMergeWithNext))]
    private Task MergeWithNextAsync()
    {
        var key = SelectedShot!.Key;
        return ActAsync("merge the shots", () => client.MergeShotAsync(ProjectId, key));
    }

    /// <summary>The selected shot changed, or another one is selected: the panel reads it again.</summary>
    private void RefreshBoardPanel()
    {
        foreach (var name in new[] { nameof(BoardShows), nameof(BoardWords), nameof(BoardStartWord), nameof(BoardEndWord), nameof(BoardCast), nameof(BoardCastChoices), nameof(BoardContinues), nameof(CanEditBoard) })
        {
            OnPropertyChanged(name);
        }
        foreach (var command in new IRelayCommand[]
        {
            StartEditShotCommand, AddCastCommand, RemoveCastCommand, StartEarlierCommand, StartLaterCommand, EndEarlierCommand, EndLaterCommand,
            SplitAtCommand, MergeWithNextCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    // Where the cuts would fall, by the rule the engine uses: in the middle of the pause between two
    // words, the segment's first shot from 0 and its last to the end of its audio. The engine checks
    // again; this only keeps a button off that would be refused.

    private IReadOnlyList<SpokenWord> SelectedWords =>
        SelectedShot is { } shot && _board is not null && _board.Words.TryGetValue(shot.Shot.SegmentId, out var words) ? words : [];

    private (List<Shot> Shots, int Index) SegmentShots()
    {
        if (!IsBoardPanel || SelectedShot is null || Selected is null)
        {
            return ([], -1);
        }
        var shots = Selected.Shots.Select(s => s.Shot.Shot).ToList();
        return (shots, shots.FindIndex(s => s.Key == SelectedShot.Key));
    }

    private bool CanMove(ShotEdge edge, int words)
    {
        var (shots, i) = SegmentShots();
        if (!CanEditShot() || i < 0 || SelectedWords.Count == 0 || (edge == ShotEdge.Start && i == 0) || (edge == ShotEdge.End && i == shots.Count - 1))
        {
            return false;
        }
        var before = edge == ShotEdge.Start ? i - 1 : i;
        var (a, b) = (shots[before], shots[before + 1]);
        var cut = b.FirstWord + words;
        return cut > a.FirstWord && cut < b.FirstWord + b.WordCount
            && Fits(Span(a.FirstWord, cut - a.FirstWord)) && Fits(Span(cut, b.FirstWord + b.WordCount - cut));
    }

    private bool CanSplitAt(int index)
    {
        var (shots, i) = SegmentShots();
        if (!CanEditShot() || i < 0 || SelectedWords.Count == 0)
        {
            return false;
        }
        var shot = shots[i];
        var end = shot.FirstWord + shot.WordCount;
        return index > shot.FirstWord && index < end && Fits(Span(shot.FirstWord, index - shot.FirstWord)) && Fits(Span(index, end - index));
    }

    private double CutAt(int index)
    {
        var words = SelectedWords;
        return index <= 0 ? 0 : (words[index - 1].End + words[index].Start) / 2;
    }

    private double SegmentSeconds => SegmentShots().Shots is { Count: > 0 } shots ? shots[^1].End : 0;

    private double Span(int first, int count) =>
        (first + count >= SelectedWords.Count ? SegmentSeconds : CutAt(first + count)) - CutAt(first);

    private bool Fits(double seconds) =>
        seconds <= (_board?.MaxClipSeconds ?? int.MaxValue) && (seconds >= Pacing.ShortestShot || seconds >= SegmentSeconds - 0.001);

    private string WordAt(int? index) => index is { } i && i >= 0 && i < SelectedWords.Count ? SelectedWords[i].Text : "";
}

/// <summary>One word of the segment in the Board panel: lit when the shot covers it.</summary>
/// <param name="Index">Its place in the segment's words, from 0: where a split puts the new shot's start.</param>
/// <param name="CanSplit">The shot can be split here: inside it, and both halves within the length limits.</param>
public sealed record BoardWord(string Text, int Index, bool IsInShot, bool CanSplit);
