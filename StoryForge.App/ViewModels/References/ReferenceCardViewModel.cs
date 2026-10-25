using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Script;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.References;

/// <summary>One image thumbnail on a card or in the panel: "image 1", its picture and state.</summary>
public sealed record ReferenceImageChoice(ReferenceImageView Image, bool IsShown)
{
    public string Label => $"image {Image.Number}";

    public string StateText => ReferenceCardViewModel.Word(Image);
}

/// <summary>
/// A reference's card, as board 9 draws it: its images, name, state, how often it is shown, what it
/// is made from, and a note when part of it was made up or nothing could be made.
/// </summary>
public sealed partial class ReferenceCardViewModel(ReferenceView view, IReadOnlyDictionary<string, string> labels, Action<ReferenceCardViewModel> select) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(StateText), nameof(Usage), nameof(SourcesText), nameof(Note), nameof(IsMissing), nameof(IsPartial), nameof(Images), nameof(Thumbnail), nameof(AccessibleName))]
    private ReferenceView _view = view;

    [ObservableProperty]
    private bool _isSelected;

    public string Id => View.Reference.Id;

    public string Name => View.Reference.Name;

    public CastKind Kind => View.Reference.Kind;

    /// <summary>The image the card stands for: the first approved one, else the first.</summary>
    private ReferenceImageView? Shown => View.Images.FirstOrDefault(i => i.State == StageState.Approved) ?? View.Images.FirstOrDefault();

    /// <summary>"approved", "review", "partly made up", "no usable picture", "running", "failed", "not made yet".</summary>
    public string StateText => Shown is { } image ? Word(image) : "not made yet";

    /// <summary>"In 5 shots".</summary>
    public string Usage => View.Reference.Shots.Count switch
    {
        0 => "In no shot",
        1 => "In 1 shot",
        var n => $"In {n} shots",
    };

    /// <summary>"from 2 pictures", "from its look alone", "no picture".</summary>
    public string SourcesText => View.Reference.DescriptionOnly
        ? "from its look alone"
        : View.Reference.Sources.Count switch
        {
            0 => "no picture",
            1 => "from 1 picture",
            var n => $"from {n} pictures",
        };

    /// <summary>What Qwen made up, or why nothing was made; empty otherwise.</summary>
    public string Note => Shown?.Image switch
    {
        { Made: ImageMaking.PartlyMadeUp } image => $"Partly made up: {image.Missing}",
        { Made: ImageMaking.NoPicture } image when Shown.Version > 0 => image.Missing,
        _ => "",
    };

    public bool IsMissing => Shown is { Version: > 0, Image.Made: ImageMaking.NoPicture };

    public bool IsPartial => Shown is { Image.Made: ImageMaking.PartlyMadeUp };

    /// <summary>The picture of the image the card stands for.</summary>
    public string? Thumbnail => Shown?.Path;

    public IReadOnlyList<ReferenceImageChoice> Images => [.. View.Images.Select((image, i) => new ReferenceImageChoice(image, i == 0))];

    /// <summary>"S01·2", "S03·1" …: the shots it is in.</summary>
    public IReadOnlyList<string> Shots => [.. View.Reference.Shots.Select(key => labels.TryGetValue(key, out var label) ? label : key)];

    [RelayCommand]
    private void Select() => select(this);

    /// <summary>What screen readers announce for the card.</summary>
    public string AccessibleName => $"{Name}: {StateText}, {Usage.ToLowerInvariant()}";

    public override string ToString() => AccessibleName;

    internal static string Word(ReferenceImageView image) => image switch
    {
        { State: StageState.Running } => "running",
        { State: StageState.Failed } => "failed",
        { Version: 0 } => "not made yet",
        { Image.Made: ImageMaking.NoPicture } => "no usable picture",
        { State: StageState.Approved } => "approved",
        { Image.Made: ImageMaking.PartlyMadeUp } => "partly made up",
        _ => ScriptMatrixViewModel.Word(image.State),
    };
}

/// <summary>A picture in the image pool: its caption, where it is from, and what it is used for.</summary>
public sealed partial class PoolPictureViewModel(PoolImageView view, Action<PoolPictureViewModel> select) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public PoolImageView View { get; } = view;

    public string Id => View.Image.Id;

    /// <summary>The wiki's caption; without one (most files have none), the file's own name.</summary>
    public string Caption => View.Image.Caption.Length > 0
        ? View.Image.Caption
        : Uri.TryCreate(View.Image.Url, UriKind.Absolute, out var url)
            ? Uri.UnescapeDataString(url.Segments[^1]).Replace('_', ' ')
            : View.Image.Url;

    public string? Path => View.Path;

    public bool TooSmall => View.Image.TooSmall;

    /// <summary>"800 × 1200 · bg3.wiki", "900 × 900 · your own".</summary>
    public string Meta => $"{View.Image.Width} × {View.Image.Height} · {(View.Image.Origin == PoolOrigin.Own ? "your own" : Site)}";

    /// <summary>"too small", "your own", "Karlach", "not used".</summary>
    public string Tag => TooSmall ? "too small" : View.UsedFor.Count > 0 ? string.Join(", ", View.UsedFor) : View.Image.Origin == PoolOrigin.Own ? "your own" : "not used";

    private string Site => Uri.TryCreate(View.Image.Page, UriKind.Absolute, out var page) ? page.Host : "";

    [RelayCommand]
    private void Select() => select(this);

    public override string ToString() => $"{Caption}: {Tag}";
}

/// <summary>A reference to pick, in "Merge with…" and "Source image for".</summary>
public sealed record ReferenceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One name from the rest of the cast: "A tavern mug · 1 shot, not key".</summary>
public sealed record OtherCastItem(string Name, string Text);
