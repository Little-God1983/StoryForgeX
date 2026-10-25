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
    [NotifyPropertyChangedFor(nameof(IsBoardRunning), nameof(IsBoardReview), nameof(IsBoardApproved), nameof(IsBoardFailed))]
    [NotifyCanExecuteChangedFor(nameof(ApproveRemainingBoardCommand), nameof(RegenerateBoardCommand), nameof(CancelBoardCommand), nameof(ApproveSegmentCommand), nameof(RegenerateSegmentCommand))]
    private StageState _boardState;

    public bool IsBoardRunning => BoardState == StageState.Running;

    public bool IsBoardReview => BoardState is StageState.NeedsReview or StageState.Stale;

    public bool IsBoardApproved => BoardState == StageState.Approved;

    public bool IsBoardFailed => BoardState == StageState.Failed;

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

    /// <summary>"Fold all" while any segment is open, else "Open all".</summary>
    public string FoldAllLabel => Rows.Any(r => r.IsOpen) ? "Fold all" : "Open all";

    [RelayCommand]
    private void FoldAll()
    {
        var open = !Rows.Any(r => r.IsOpen);
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
            if (BoardState != StageState.Running)
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
                    row.Shots.Insert(i, new ShotRowViewModel(shots[i], SelectShot));
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
        ApproveRemainingBoardCommand.NotifyCanExecuteChanged();
        RegenerateBoardCommand.NotifyCanExecuteChanged();
    }

    private void SelectShot(ShotRowViewModel shot) => Select(Rows.FirstOrDefault(r => r.Shots.Contains(shot)), PipelineStage.Storyboard, shot);

    /// <summary>"6.1", "20": a shot's length in the panel and the pacing line.</summary>
    internal static string Seconds(double seconds) => Math.Round(seconds, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
