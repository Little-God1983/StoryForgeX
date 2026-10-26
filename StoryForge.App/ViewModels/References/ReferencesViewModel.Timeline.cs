using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.References;

/// <summary>A shot's column on the timeline: as wide as it lasts, where it starts, how full it is.</summary>
public sealed partial class TimelineShotViewModel(TimelineShot shot) : ObservableObject
{
    public string Key => shot.Key;

    /// <summary>"S01·2".</summary>
    public string Label => shot.Label;

    /// <summary>How long it lasts: its column's share of the width.</summary>
    public double Seconds => shot.Seconds;

    /// <summary>"0:04".</summary>
    public string StartText => ReferencesViewModel.Clock(shot.Start);

    /// <summary>"3/4": who and what is in it, references or not, out of what Minimax takes per clip.</summary>
    public string CountText => $"{shot.Cast.Count}/{Shot.MaxCast}";

    public bool IsFull => shot.Cast.Count >= Shot.MaxCast;

    /// <summary>The picked reference is in it.</summary>
    [ObservableProperty]
    private bool _isLit;

    public string ToolTip => $"{Label} · starts at {StartText} · {CountText} in it";
}

/// <summary>One reference in one shot: a bar when it is in it; a click puts it in or takes it out.</summary>
public sealed class TimelineCellViewModel(string referenceId, string name, TimelineShot shot, bool isOn)
{
    public string ReferenceId => referenceId;

    public string ShotKey => shot.Key;

    public double Seconds => shot.Seconds;

    public bool IsOn => isOn;

    /// <summary>Not in it, and the shot is full: it takes no more.</summary>
    public bool IsBlocked => !IsOn && shot.Cast.Count >= Shot.MaxCast;

    public string AccessibleName => $"{name} in {shot.Label}: " + (IsOn
        ? "used, click to remove"
        : IsBlocked ? $"the shot is full, {Shot.MaxCast} of {Shot.MaxCast}" : "not used, click to add");
}

/// <summary>A reference's row on the timeline: its name, how many shots it is in, a cell per shot.</summary>
public sealed partial class TimelineRowViewModel(ReferenceCardViewModel card, IReadOnlyList<TimelineCellViewModel> cells, Action<ReferenceCardViewModel> pick) : ObservableObject
{
    public string Id => card.Id;

    public string Name => card.Name;

    public CastKind Kind => card.Kind;

    public int Count => card.View.Reference.Shots.Count;

    public IReadOnlyList<TimelineCellViewModel> Cells { get; } = cells;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Another reference is picked: this row steps back.</summary>
    [ObservableProperty]
    private bool _isDimmed;

    [RelayCommand]
    private void Pick() => pick(card);
}

/// <summary>
/// The timeline (board 9): which reference goes into which shot. A row per reference, a column per
/// shot as wide as it lasts, a count out of 4 under each; it changes the shots' who and what, the
/// same the Board panel shows.
/// </summary>
public sealed partial class ReferencesViewModel
{
    [ObservableProperty]
    private bool _isTimelineOpen = true;

    public ObservableCollection<TimelineShotViewModel> TimelineShots { get; } = [];

    public ObservableCollection<TimelineRowViewModel> TimelineRows { get; } = [];

    /// <summary>"14 shots · 1:12 · S04·2 full (4/4)".</summary>
    [ObservableProperty]
    private string _timelineSummary = "";

    /// <summary>There are shots and references to put in them.</summary>
    public bool HasTimeline => TimelineShots.Count > 0 && TimelineRows.Count > 0;

    [RelayCommand]
    private void ToggleTimeline() => IsTimelineOpen = !IsTimelineOpen;

    /// <summary>"1:05", from seconds, as the other screens show times.</summary>
    internal static string Clock(double seconds) => Script.ScriptMatrixViewModel.Clock(seconds);

    /// <summary>Made again from the view, after the cards: a row per card, in their order.</summary>
    private void FillTimeline(ReferencesView view)
    {
        TimelineShots.Clear();
        foreach (var shot in view.Shots)
        {
            TimelineShots.Add(new TimelineShotViewModel(shot));
        }
        TimelineRows.Clear();
        foreach (var card in Cards)
        {
            var shots = card.View.Reference.Shots;
            TimelineRows.Add(new TimelineRowViewModel(card, [.. view.Shots.Select(s => new TimelineCellViewModel(card.Id, card.Name, s, shots.Contains(s.Key)))], SelectCard));
        }
        var full = TimelineShots.Where(s => s.IsFull).Select(s => s.Label).ToList();
        var fullText = full.Count switch
        {
            0 => "no shot full",
            <= 3 => $"{string.Join(" ", full)} full ({Shot.MaxCast}/{Shot.MaxCast})",
            _ => $"{full.Count} shots full ({Shot.MaxCast}/{Shot.MaxCast})",
        };
        TimelineSummary = $"{view.Shots.Count} shots · {Clock(view.Shots.Sum(s => s.Seconds))} · {fullText}";
        OnPropertyChanged(nameof(HasTimeline));
    }

    /// <summary>The picked reference's row stands out, and the shots it is in light up.</summary>
    private void RefreshTimeline()
    {
        var picked = SelectedCard;
        var lit = picked?.View.Reference.Shots ?? [];
        foreach (var shot in TimelineShots)
        {
            shot.IsLit = lit.Contains(shot.Key);
        }
        foreach (var row in TimelineRows)
        {
            row.IsSelected = row.Id == picked?.Id;
            row.IsDimmed = picked is not null && !row.IsSelected;
        }
    }

    private bool CanToggleCell(TimelineCellViewModel? cell) => cell is { IsBlocked: false } && !_acting;

    [RelayCommand(CanExecute = nameof(CanToggleCell))]
    private Task ToggleCellAsync(TimelineCellViewModel? cell) =>
        ActAsync("change the shot", () => client.SetReferenceInShotAsync(ProjectId, cell!.ReferenceId, cell.ShotKey, !cell.IsOn));
}
