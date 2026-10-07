using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.App.ViewModels.Profiles;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.NewProject;

/// <summary>
/// A dropdown entry whose text can change in place ("1344 × 768 · profile default" when another
/// profile is picked). The entries stay the same objects: swapping the list made WPF drop the
/// selection and show a blank field (seen on screen, 2026-10-07).
/// </summary>
public sealed partial class OptionItem(string label) : ObservableObject
{
    [ObservableProperty]
    private string _label = label;

    public override string ToString() => Label;
}

/// <summary>
/// "Generation size": the profile's size for the project's aspect, or a custom one for this
/// project. Changing the aspect goes back to the profile's size for the new aspect.
/// </summary>
public sealed partial class GenerationSizeViewModel : ObservableValidator
{
    /// <summary>Used when the profile has no size for the aspect.</summary>
    private static readonly Dictionary<string, GenerationSize> BuiltIn = new()
    {
        ["16:9"] = new("16:9", 1344, 768),
        ["9:16"] = new("9:16", 768, 1344),
        ["1:1"] = new("1:1", 1024, 1024),
    };

    private GenerationSize _default = BuiltIn["16:9"];
    private bool _defaultFromProfile;

    public const int ProfileDefaultIndex = 0;
    public const int CustomIndex = 1;

    public GenerationSizeViewModel() => Options = [new(DefaultLabel()), new("Custom…")];

    /// <summary>The two dropdown entries: "1344 × 768 · profile default" and "Custom…".</summary>
    public IReadOnlyList<OptionItem> Options { get; }

    private string DefaultLabel() => $"{_default.Width} × {_default.Height} · {(_defaultFromProfile ? "profile" : "built-in")} default";

    private int _selectedIndex = ProfileDefaultIndex;

    /// <summary>Which entry is picked; anything but the two entries (WPF's -1 for "none") is ignored.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value is ProfileDefaultIndex or CustomIndex && SetProperty(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(IsCustom));
                OnSelectedIndexChanged(value);
            }
        }
    }

    public bool IsCustom => SelectedIndex == CustomIndex;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GenerationSizeViewModel), nameof(ValidatePixels))]
    private string _customWidth = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GenerationSizeViewModel), nameof(ValidatePixels))]
    private string _customHeight = "";

    /// <summary>The size the project will use; null while a custom size is not a valid number.</summary>
    public GenerationSize? Value => IsCustom
        ? NumberText.Parse(CustomWidth) is { } w and >= 1 && NumberText.Parse(CustomHeight) is { } h and >= 1
            ? new GenerationSize(_default.Aspect, w, h)
            : null
        : _default;

    /// <summary>Takes the profile's size for <paramref name="aspect"/> (or the built-in one) and drops a custom size.</summary>
    public void UseDefaultFor(string aspect, ProfileContent? profile)
    {
        var fromProfile = profile?.Sizes.FirstOrDefault(s => s.Aspect == aspect);
        _defaultFromProfile = fromProfile is not null;
        _default = fromProfile ?? BuiltIn[aspect];
        // A custom size was typed for the old aspect; the next "Custom…" starts from the new default.
        CustomWidth = "";
        CustomHeight = "";
        SelectedIndex = ProfileDefaultIndex;
        Options[ProfileDefaultIndex].Label = DefaultLabel();
        OnPropertyChanged(nameof(Value));
    }

    private void OnSelectedIndexChanged(int value)
    {
        // A custom size starts from the default, so only the number that differs is typed.
        if (value == CustomIndex && CustomWidth.Length == 0 && CustomHeight.Length == 0)
        {
            CustomWidth = NumberText.Format(_default.Width);
            CustomHeight = NumberText.Format(_default.Height);
        }
        // The custom fields only count while "Custom…" is picked.
        ValidateAllProperties();
        OnPropertyChanged(nameof(Value));
    }

    partial void OnCustomWidthChanged(string value) => OnPropertyChanged(nameof(Value));

    partial void OnCustomHeightChanged(string value) => OnPropertyChanged(nameof(Value));

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
            if (value is GenerationSizeViewModel.ProfileDefaultIndex or GenerationSizeViewModel.CustomIndex
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

    public void UseDefault(ProfileContent? profile)
    {
        _defaultFromProfile = profile?.MaxClipSeconds is not null;
        _default = profile?.MaxClipSeconds ?? BuiltInSeconds;
        Options[GenerationSizeViewModel.ProfileDefaultIndex].Label = DefaultLabel();
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
