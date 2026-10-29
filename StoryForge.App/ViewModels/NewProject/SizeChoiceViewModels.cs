using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Profiles;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.NewProject;

/// <summary>
/// A dropdown entry whose text can change in place ("1376 × 768 · delivery size" when another
/// delivery is picked). The entries stay the same objects: swapping the list made WPF drop the
/// selection and show a blank field (seen on screen, 2026-10-07).
/// </summary>
public sealed partial class OptionItem(string label) : ObservableObject
{
    [ObservableProperty]
    private string _label = label;

    public override string ToString() => Label;
}

/// <summary>
/// "Generation size": the delivery size, or a custom one for this project. Stills and clips are
/// made at the delivery size, not upscaled later (#13 was dropped: video upscaling looks poor).
/// Changing the aspect goes back to the delivery size.
/// </summary>
/// <param name="scaledUp">What a size smaller than the delivery means, e.g. "Assembly scales the clips up."</param>
public sealed partial class GenerationSizeViewModel(string scaledUp) : ObservableValidator
{
    /// <summary>Models take sizes in multiples of 32: a 1920 × 1080 delivery is made at 1920 × 1088 and cropped.</summary>
    public const int Step = 32;

    /// <summary>Until the page hands over its delivery.</summary>
    private static readonly GenerationSize FullHd = new("16:9", 1920, 1080);

    private GenerationSize _delivery = FullHd;

    /// <summary>The delivery rounded up to <see cref="Step"/>: the size picked when "Custom…" is not.</summary>
    private GenerationSize Default => RoundUp(_delivery);

    public const int DefaultIndex = 0;
    public const int CustomIndex = 1;

    /// <summary>The two dropdown entries: "1920 × 1088 · delivery size" and "Custom…".</summary>
    public IReadOnlyList<OptionItem> Options { get; } = [new(DefaultLabel(RoundUp(FullHd))), new("Custom…")];

    private static string DefaultLabel(GenerationSize size) => $"{size.Width} × {size.Height} · delivery size";

    /// <summary>"Smaller than the delivery: Assembly scales the clips up."; null when the size is not smaller.</summary>
    public string? Warning => Value is { } size && (size.Width < _delivery.Width || size.Height < _delivery.Height)
        ? $"Smaller than the delivery: {scaledUp}"
        : null;

    private int _selectedIndex = DefaultIndex;

    /// <summary>Which entry is picked; anything but the two entries (WPF's -1 for "none") is ignored.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value is DefaultIndex or CustomIndex && SetProperty(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(IsCustom));
                OnSelectedIndexChanged(value);
            }
        }
    }

    public bool IsCustom => SelectedIndex == CustomIndex;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(Value), nameof(Warning))]
    [CustomValidation(typeof(GenerationSizeViewModel), nameof(ValidatePixels))]
    private string _customWidth = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(Value), nameof(Warning))]
    [CustomValidation(typeof(GenerationSizeViewModel), nameof(ValidatePixels))]
    private string _customHeight = "";

    /// <summary>The size the project will use; null while a custom size is not valid or not in the video's shape.</summary>
    public GenerationSize? Value => Problem is null ? IsCustom ? Custom() : Default : null;

    /// <summary>Why <see cref="Value"/> is null, e.g. "a size that fits 9:16"; null when it is not.</summary>
    public string? Problem => !IsCustom ? null
        : Custom() is not { } custom ? "a valid size"
        : !FitsAspect(custom) ? $"a size that fits {_delivery.Aspect}"
        : custom.Width % Step != 0 || custom.Height % Step != 0 ? $"a size in multiples of {Step}{Example(RoundUp(custom))}"
        : null;

    /// <summary>" (e.g. 1376 × 768)": the rounded size, when it still has the video's shape.</summary>
    private static string Example(GenerationSize rounded) => FitsAspect(rounded) ? $" (e.g. {rounded.Width} × {rounded.Height})" : "";

    private GenerationSize? Custom() =>
        NumberText.Parse(CustomWidth) is { } w and >= 1 and <= 16384 && NumberText.Parse(CustomHeight) is { } h and >= 1 and <= 16384
            ? new GenerationSize(_delivery.Aspect, w, h)
            : null;

    /// <summary>Landscape for 16:9, portrait for 9:16, square for 1:1. Models' sizes are not exact ratios (1344 × 768).</summary>
    public static bool FitsAspect(GenerationSize size) => size.Aspect switch
    {
        "16:9" => size.Width > size.Height,
        "9:16" => size.Width < size.Height,
        _ => size.Width == size.Height,
    };

    /// <summary>The delivery rounded up to <see cref="Step"/>: 1366 × 768 → 1376 × 768.</summary>
    public static GenerationSize RoundUp(GenerationSize delivery) =>
        delivery with { Width = (delivery.Width + Step - 1) / Step * Step, Height = (delivery.Height + Step - 1) / Step * Step };

    /// <summary>
    /// Takes the delivery's size. A custom size in use stays for another delivery of the same aspect;
    /// it is dropped for another aspect, and when "Custom…" is not picked.
    /// </summary>
    public void UseDelivery(GenerationSize delivery)
    {
        var before = Default;
        _delivery = delivery;
        if (delivery.Aspect != before.Aspect || !IsCustom)
        {
            // A custom size typed for another shape, or one not in use; the next "Custom…" starts from the new default.
            CustomWidth = "";
            CustomHeight = "";
            SelectedIndex = DefaultIndex;
        }
        else if (Custom() == before)
        {
            // Filled in and left as it was: nothing typed to keep.
            CustomWidth = NumberText.Format(Default.Width);
            CustomHeight = NumberText.Format(Default.Height);
        }
        Options[DefaultIndex].Label = DefaultLabel(Default);
        RaiseValue();
    }

    private void OnSelectedIndexChanged(int value)
    {
        // A custom size starts from the default, so only the number that differs is typed.
        if (value == CustomIndex && CustomWidth.Length == 0 && CustomHeight.Length == 0)
        {
            CustomWidth = NumberText.Format(Default.Width);
            CustomHeight = NumberText.Format(Default.Height);
        }
        // The custom fields only count while "Custom…" is picked.
        ValidateAllProperties();
        RaiseValue();
    }

    private void RaiseValue()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Warning));
    }

    public static ValidationResult? ValidatePixels(string text, ValidationContext context) =>
        context.ObjectInstance is GenerationSizeViewModel { IsCustom: false }
            ? ValidationResult.Success
            : NumberText.Check(text, 1, 16384, optional: false, "1 to 16384.");
}

/// <summary>"Max clip length": the video profile's, or a custom one for this project.</summary>
public sealed partial class MaxClipViewModel : ObservableValidator
{
    private const int BuiltInSeconds = 20;

    private int _default = BuiltInSeconds;
    private bool _defaultFromProfile;

    public MaxClipViewModel() => Options = [new(DefaultLabel()), new("Custom…")];

    public IReadOnlyList<OptionItem> Options { get; }

    private string DefaultLabel() => $"{_default} s · {(_defaultFromProfile ? "profile" : "built-in")} default";

    private int _selectedIndex;

    /// <summary>As <see cref="GenerationSizeViewModel.SelectedIndex"/>: -1 from a rebuilt dropdown is ignored.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value is GenerationSizeViewModel.DefaultIndex or GenerationSizeViewModel.CustomIndex
                && SetProperty(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(Value));
                OnSelectedIndexChanged();
            }
        }
    }

    public bool IsCustom => SelectedIndex == GenerationSizeViewModel.CustomIndex;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(Value))]
    [CustomValidation(typeof(MaxClipViewModel), nameof(ValidateSeconds))]
    private string _customSeconds = "";

    public int? Value => IsCustom ? NumberText.Parse(CustomSeconds) is { } s and >= 1 and <= 600 ? s : null : _default;

    /// <summary>
    /// Takes the profile's clip length. A custom one is dropped, as the size's is, unless
    /// <paramref name="keepCustom"/>: a length typed for one model may be too long for another.
    /// </summary>
    public void UseDefault(ProfileContent? profile, bool keepCustom = false)
    {
        _defaultFromProfile = profile?.MaxClipSeconds is not null;
        _default = profile?.MaxClipSeconds ?? BuiltInSeconds;
        if (!keepCustom)
        {
            CustomSeconds = "";
            SelectedIndex = GenerationSizeViewModel.DefaultIndex;
        }
        Options[GenerationSizeViewModel.DefaultIndex].Label = DefaultLabel();
        OnPropertyChanged(nameof(Value));
    }

    private void OnSelectedIndexChanged()
    {
        if (IsCustom && CustomSeconds.Length == 0)
        {
            CustomSeconds = NumberText.Format(_default);
        }
        ValidateAllProperties();
    }

    public static ValidationResult? ValidateSeconds(string text, ValidationContext context) =>
        context.ObjectInstance is MaxClipViewModel { IsCustom: false }
            ? ValidationResult.Success
            : NumberText.Check(text, 1, 600, optional: false, "1 to 600 seconds.");
}

/// <summary>
/// "Pacing": how long shots should be. The storyboard profile's, or a custom one for this
/// project; only a custom one goes into the setup, so the project keeps following its profile.
/// </summary>
public sealed partial class PacingViewModel : ObservableObject
{
    private Pacing _default = Pacing.Default;
    private bool _defaultFromProfile;

    public PacingViewModel() => Options = [new(DefaultLabel()), new("Custom…")];

    public IReadOnlyList<OptionItem> Options { get; }

    private string DefaultLabel() => $"{_default} · {(_defaultFromProfile ? "profile" : "built-in")} default";

    private int _selectedIndex;

    /// <summary>As <see cref="GenerationSizeViewModel.SelectedIndex"/>: -1 from a rebuilt dropdown is ignored.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value is GenerationSizeViewModel.DefaultIndex or GenerationSizeViewModel.CustomIndex
                && SetProperty(ref _selectedIndex, value))
            {
                if (IsCustom && CustomMin.Length + CustomTypical.Length + CustomMax.Length == 0)
                {
                    (CustomMin, CustomTypical, CustomMax) = (NumberText.Format(_default.MinSeconds), NumberText.Format(_default.TypicalSeconds), NumberText.Format(_default.MaxSeconds));
                }
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(Override));
                OnPropertyChanged(nameof(Effective));
            }
        }
    }

    public bool IsCustom => SelectedIndex == GenerationSizeViewModel.CustomIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Override), nameof(Effective))]
    private string _customMin = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Override), nameof(Effective))]
    private string _customTypical = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Override), nameof(Effective))]
    private string _customMax = "";

    private Pacing? Custom =>
        NumberText.Parse(CustomMin) is { } min && NumberText.Parse(CustomTypical) is { } typical && NumberText.Parse(CustomMax) is { } max
            ? new Pacing(min, typical, max)
            : null;

    /// <summary>What goes into the setup: the custom pacing, or null to keep the profile's.</summary>
    public Pacing? Override => IsCustom ? Custom : null;

    /// <summary>The pacing the storyboard will use; null while a custom one is not a number.</summary>
    public Pacing? Effective => IsCustom ? Custom : _default;

    /// <summary>
    /// Why the custom pacing cannot be used; null when it can. The profile's is never a problem: the
    /// storyboard holds it to the project's clips (3–8 s with clips of 6 s becomes 3–6 s).
    /// </summary>
    public string? Problem(int? maxClipSeconds) =>
        !IsCustom ? null : Custom is { } pacing ? pacing.Problem(maxClipSeconds) : "Every length is a whole number of seconds.";

    /// <summary>
    /// Takes the profile's pacing. A custom one is dropped, unless <paramref name="keepCustom"/>
    /// (a new version of the same profile).
    /// </summary>
    public void UseDefault(ProfileContent? profile, bool keepCustom = false)
    {
        _defaultFromProfile = profile?.Pacing is not null;
        _default = profile?.Pacing ?? Pacing.Default;
        if (!keepCustom)
        {
            (CustomMin, CustomTypical, CustomMax) = ("", "", "");
            SelectedIndex = GenerationSizeViewModel.DefaultIndex;
        }
        Options[GenerationSizeViewModel.DefaultIndex].Label = DefaultLabel();
        OnPropertyChanged(nameof(Effective));
    }
}
