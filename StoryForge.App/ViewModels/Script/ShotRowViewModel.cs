using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>One shot under its segment, as board 2b draws it: "S01·2", what it shows, its Board cell and its Still cell.</summary>
public sealed partial class ShotRowViewModel(ShotView shot, Action<ShotRowViewModel> select, Action<StillCellViewModel>? selectStill = null) : ObservableObject
{
    private StillCellViewModel? _still;

    /// <summary>The shot's still; it shows "—" before the stills are made.</summary>
    public StillCellViewModel Still => _still ??= new(selectStill ?? (_ => { }), () => Label);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Description), nameof(State), nameof(DisplayState), nameof(StateText), nameof(VersionText), nameof(AccessibleName))]
    private ShotView _shot = shot;

    /// <summary>The Board cell is the one selected.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Stays the same when the shot moves up or down a number.</summary>
    public string Key => Shot.Key;

    public string Label => Shot.Label;

    public string Description => Shot.Shot.Description;

    public StageState State => Shot.State;

    /// <summary>What the cell shows: stale when it is out of date.</summary>
    public StageState DisplayState => ScriptMatrixViewModel.Shown(Shot.State, Shot.Stale);

    public string StateText => ScriptMatrixViewModel.Word(DisplayState);

    public string VersionText => $"v{Shot.Version}";

    [RelayCommand]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the Board cell; it follows the cell as it changes.</summary>
    public string AccessibleName => $"{Label} board: {StateText}, version {Shot.Version}";

    public override string ToString() => AccessibleName;
}
