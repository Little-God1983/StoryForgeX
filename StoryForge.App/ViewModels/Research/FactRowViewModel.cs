using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Research;

/// <summary>One fact in the list: its words, its source, its weight, and whether it is left out.</summary>
public sealed partial class FactRowViewModel : ObservableObject
{
    private readonly Func<FactRowViewModel, FactChange, Task> _change;
    private readonly Action<FactRowViewModel> _select;

    public FactRowViewModel(Fact fact, Func<FactRowViewModel, FactChange, Task> change, Action<FactRowViewModel> select)
    {
        _change = change;
        _select = select;
        Id = fact.Id;
        Update(fact);
    }

    public string Id { get; }

    [ObservableProperty]
    private string _statement = "";

    [ObservableProperty]
    private string _sourceUrl = "";

    [ObservableProperty]
    private string _quote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightMeaning), nameof(IsMust), nameof(IsLow))]
    [NotifyCanExecuteChangedFor(nameof(RaiseWeightCommand), nameof(LowerWeightCommand))]
    private int _weight;

    [ObservableProperty]
    private bool _leftOut;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"bg3.wiki/wiki/Soul_Coins:_A_Treatise": the source without its scheme, readable.</summary>
    public string SourceDisplay => Uri.UnescapeDataString(
        SourceUrl.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase));

    public bool IsMust => Weight >= Fact.MustWeight;

    public bool IsLow => Weight <= 3;

    public string WeightMeaning => Weight switch
    {
        >= Fact.MustWeight => "must be in the video",
        Fact.MinWeight => "only if there is time left",
        > Fact.DefaultWeight => "more important than normal",
        < Fact.DefaultWeight => "less important than normal",
        _ => "normal",
    };

    public void Update(Fact fact)
    {
        Statement = fact.Statement;
        SourceUrl = fact.SourceUrl;
        Quote = fact.Quote;
        Weight = fact.Weight;
        LeftOut = fact.LeftOut;
        OnPropertyChanged(nameof(SourceDisplay));
    }

    private bool CanRaise() => Weight < Fact.MaxWeight;

    private bool CanLower() => Weight > Fact.MinWeight;

    [RelayCommand(CanExecute = nameof(CanRaise))]
    private Task RaiseWeightAsync() => _change(this, new FactChange(Weight: Weight + 1));

    [RelayCommand(CanExecute = nameof(CanLower))]
    private Task LowerWeightAsync() => _change(this, new FactChange(Weight: Weight - 1));

    [RelayCommand]
    private Task LeaveOutAsync() => _change(this, new FactChange(LeftOut: true));

    [RelayCommand]
    private Task PutBackAsync() => _change(this, new FactChange(LeftOut: false));

    [RelayCommand]
    private void Select() => _select(this);
}
