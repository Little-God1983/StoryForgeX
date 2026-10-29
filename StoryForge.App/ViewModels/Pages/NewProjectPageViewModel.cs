using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.NewProject;
using StoryForge.App.ViewModels.Settings;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>
/// The New project screen: brief, providers and profiles per stage, output, run plan. "Start run"
/// saves all of it, with the exact profile versions, and hands the project to the result matrix.
/// </summary>
public sealed partial class NewProjectPageViewModel : PageViewModel
{
    private static readonly IReadOnlyList<Choice<string>> TextProviders = [new("Claude CLI", "Claude CLI"), new("LM Studio", "LM Studio (OpenAI API)")];
    private static readonly IReadOnlyList<Choice<string>> ComfyUi = [new("ComfyUI", "ComfyUI (local)")];

    public static IReadOnlyList<Choice<Consistency>> ConsistencyChoices { get; } =
    [
        new(Consistency.ReferenceImages, "From reference images"),
        new(Consistency.TextToImageOnly, "Text-to-image only (no references)"),
    ];

    public const int CustomTarget = -1;

    public static IReadOnlyList<Choice<int>> TargetChoices { get; } =
        [new(60, "1 min"), new(180, "3 min"), new(240, "4 min"), new(300, "5 min"), new(CustomTarget, "Custom…")];

    public static IReadOnlyList<string> Languages { get; } = ["English", "Deutsch"];

    public static IReadOnlyList<Choice<AssemblyTarget>> AssemblyChoices { get; } =
    [
        new(AssemblyTarget.FfmpegWithFcpxml, "FFmpeg + FCPXML export"),
        new(AssemblyTarget.DavinciResolve, "DaVinci Resolve (MCP)"),
    ];

    private readonly IStoryForgeClient _client;
    private readonly List<Task> _pending = [];
    private readonly Dictionary<ProfileSlotViewModel, ProfileSummary?> _applied = [];

    /// <summary>The picked profiles' content, kept so research can tell the sources typed from the old profile's.</summary>
    private readonly Dictionary<ProfileSlotViewModel, ProfileContent?> _content = [];

    public NewProjectPageViewModel(IStoryForgeClient client)
        : base("New project", "Projects / New project", "")
    {
        _client = client;
        ResearchSlot = new ProfileSlotViewModel(ProfileKind.Research, "Research profile");
        StoryboardSlot = new ProfileSlotViewModel(ProfileKind.Storyboard, "Storyboard profile");
        ImageSlot = new ProfileSlotViewModel(ProfileKind.Image, "Image profile");
        VideoSlot = new ProfileSlotViewModel(ProfileKind.Video, "Video profile");
        Research = new StageCardViewModel("Research", 0, TextProviders, [new("", "Provider default")], ResearchSlot);
        Script = new StageCardViewModel("Script", 1, TextProviders, [new("", "Provider default")],
            new(ProfileKind.Script, "Script profile"), StoryboardSlot);
        Voice = new StageCardViewModel("Voice (TTS)", 2, ComfyUi, [new("Breeze TTS", "Breeze TTS")],
            new ProfileSlotViewModel(ProfileKind.Voice, "Voice profile"));
        // Reference images are image profiles too: ones whose workflow edits a found picture.
        References = new StageCardViewModel("Image references", 3, ComfyUi,
            [new("Qwen Image 2.1 edit", "Qwen Image 2.1 edit"), new("Krea 2 edit", "Krea 2 edit")],
            new ProfileSlotViewModel(ProfileKind.Image, "Reference profile"));
        Stills = new StageCardViewModel("Still images", 4, ComfyUi, [new("Qwen Image 2.1", "Qwen Image 2.1"), new("Krea 2 Turbo", "Krea 2 Turbo")],
            ImageSlot);
        Clips = new StageCardViewModel("Video clips", 5, ComfyUi, [new("Minimax H3", "Minimax H3"), new("LTX 2.5", "LTX 2.5 (fallback)")],
            VideoSlot);
        Cards = [Research, Script, Voice, References, Stills, Clips];

        ResearchSlot.PropertyChanged += (_, e) => OnSlotChanged(e, ResearchSlot, ApplyResearchProfile);
        StoryboardSlot.PropertyChanged += (_, e) => OnSlotChanged(e, StoryboardSlot,
            (content, _, sameProfile) => Pacing.UseDefault(content, keepCustom: sameProfile));
        // The sizes follow the delivery, not the profiles: the image profile has no defaults to load.
        VideoSlot.PropertyChanged += (_, e) => OnSlotChanged(e, VideoSlot,
            (content, _, sameProfile) => MaxClip.UseDefault(content, keepCustom: sameProfile));
        foreach (var slot in Cards.SelectMany(c => c.Slots))
        {
            slot.PropertyChanged += (_, _) => UpdateMissing();
        }
        foreach (var watched in new INotifyPropertyChanged[] { StillSize, ClipSize, MaxClip, Pacing })
        {
            watched.PropertyChanged += (_, _) => UpdateMissing();
        }

        _selectedDelivery = DeliveryChoices[1];   // 1080p
        StillSize.UseDelivery(_selectedDelivery.Value);
        ClipSize.UseDelivery(_selectedDelivery.Value);
        RunPlan =
        [
            new(PipelineStage.Research, "Research", "Sources + fact sheet", gate: true),
            new(PipelineStage.Script, "Script", "Segments + narration", gate: true),
            new(PipelineStage.Voice, "Voice", "TTS · word timings", gate: false),
            new(PipelineStage.Storyboard, "Storyboard", "Shots timed to the audio · gate required", gate: true),
            new(PipelineStage.References, "References", ReferencesVia(), gate: true),
            new(PipelineStage.Stills, "Stills", "One image per shot", gate: true),
            new(PipelineStage.Clips, "Clips", "Image-to-video", gate: false),
            new(PipelineStage.Assembly, "Assembly", "LLM cut list → FFmpeg or Resolve", gate: true),
            new(PipelineStage.Publish, "Publish to CAX", "Content Automator X tenant", gate: false),
        ];
        References.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StageCardViewModel.Model))
            {
                UpdateReferencesVia();
            }
        };
        UpdateMissing();
    }

    // ── Brief ──

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _brief = "";

    /// <summary>Starts as the research profile's sources; the chips above the brief.</summary>
    public ObservableCollection<string> ResearchSources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSourceCommand))]
    private string _newSource = "";

    // ── Providers ──

    public StageCardViewModel Research { get; }

    public StageCardViewModel Script { get; }

    public StageCardViewModel Voice { get; }

    public StageCardViewModel References { get; }

    public StageCardViewModel Stills { get; }

    public StageCardViewModel Clips { get; }

    public IReadOnlyList<StageCardViewModel> Cards { get; }

    public ProfileSlotViewModel ResearchSlot { get; }

    public ProfileSlotViewModel ImageSlot { get; }

    public ProfileSlotViewModel VideoSlot { get; }

    public ProfileSlotViewModel StoryboardSlot { get; }

    [ObservableProperty]
    private Consistency _consistency = Consistency.ReferenceImages;

    /// <summary>The run stops on the proposed reference list before any image is made.</summary>
    [ObservableProperty]
    private bool _reviewReferenceList = true;

    public GenerationSizeViewModel StillSize { get; } = new("the stills are scaled up for the clips.");

    public GenerationSizeViewModel ClipSize { get; } = new("Assembly scales the clips up.");

    public MaxClipViewModel MaxClip { get; } = new();

    /// <summary>How long shots should be: the storyboard profile's, or a custom one.</summary>
    public PacingViewModel Pacing { get; } = new();

    /// <summary>Why the custom pacing cannot be used; null when it can.</summary>
    public string? PacingProblem => Pacing.Problem(MaxClip.Value);

    /// <summary>"about 48 shots → 48 stills, 48 clips": the target length over the typical shot, before anything is spent.</summary>
    public string ShotEstimate => TargetLength() is { } target && Pacing.Effective?.HeldTo(MaxClip.Value ?? int.MaxValue) is { TypicalSeconds: > 0 } pacing
        && Math.Max(1, (int)Math.Round((double)target / pacing.TypicalSeconds)) is var shots
        ? $"about {shots} shots → {shots} stills, {shots} clips"
        : "";

    // ── Output ──

    [ObservableProperty]
    private string _aspect = "16:9";

    /// <summary>
    /// Delivery resolutions for the aspect: 768p, 1080p and 1440p tiers. Stills and clips are made
    /// at this size, so 4K is left out until we see how 1440p performs.
    /// </summary>
    public IReadOnlyList<Choice<GenerationSize>> DeliveryChoices => Aspect switch
    {
        "9:16" => [Delivery("9:16", 768, 1366), Delivery("9:16", 1080, 1920), Delivery("9:16", 1440, 2560)],
        "1:1" => [Delivery("1:1", 768, 768), Delivery("1:1", 1080, 1080), Delivery("1:1", 1440, 1440)],
        _ => [Delivery("16:9", 1366, 768), Delivery("16:9", 1920, 1080), Delivery("16:9", 2560, 1440)],
    };

    private Choice<GenerationSize> _selectedDelivery;

    /// <summary>Null is ignored: the dropdown writes null when its list is swapped for another aspect's.</summary>
    public Choice<GenerationSize> SelectedDelivery
    {
        get => _selectedDelivery;
        set
        {
            if (value is not null && SetProperty(ref _selectedDelivery, value))
            {
                StillSize.UseDelivery(value.Value);
                ClipSize.UseDelivery(value.Value);
                UpdateMissing();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTarget))]
    private int _targetSeconds = 240;

    public bool IsCustomTarget => TargetSeconds == CustomTarget;

    /// <summary>Minutes, when the target length is custom; "2.5" is two and a half minutes.</summary>
    [ObservableProperty]
    private string _customTargetMinutes = "";

    [ObservableProperty]
    private string _language = "English";

    [ObservableProperty]
    private AssemblyTarget _assembly = AssemblyTarget.FfmpegWithFcpxml;

    // ── Run plan ──

    public IReadOnlyList<RunStageViewModel> RunPlan { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunThrough))]
    private bool _stopAtGates = true;

    public bool RunThrough
    {
        get => !StopAtGates;
        set => StopAtGates = !value;
    }

    /// <summary>What still keeps "Start run" off, e.g. "Needs a name and a video profile."; null when ready.</summary>
    public string? MissingText { get; private set; }

    public bool CanStart => MissingText is null && !IsStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isStarting;

    /// <summary>Why the last start or profile load failed; null when all is well.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Raised with the saved project after "Start run".</summary>
    public event EventHandler<Project>? ProjectStarted;

    /// <summary>The profile loads the pickers started; tests await them.</summary>
    public Task WhenSettled() => Task.WhenAll(_pending.ToArray());

    /// <summary>Reads the profiles again (some may have been made since) and fills the pickers.</summary>
    public async Task ShowAsync()
    {
        try
        {
            var profiles = await _client.GetProfilesAsync();
            // Cleared before the pickers are filled: filling them loads content, which can fail
            // and must be able to say so.
            Error = null;
            foreach (var slot in Cards.SelectMany(c => c.Slots))
            {
                slot.SetChoices(profiles);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading profiles for New project failed: {ex}");
            Error = $"Could not load the profiles: {ex.Message}";
        }
        await WhenSettled();
    }

    private bool CanAddSource() => NewSource.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddSource))]
    private void AddSource()
    {
        var source = NewSource.Trim();
        if (!ResearchSources.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            ResearchSources.Add(source);
        }
        NewSource = "";
    }

    [RelayCommand]
    private void RemoveSource(string source) => ResearchSources.Remove(source);

    [RelayCommand]
    private void ApplyToLaterStages(StageCardViewModel source)
    {
        foreach (var card in Cards.Where(c => c.Order > source.Order))
        {
            card.ApplyFrom(source);
        }
    }

    [RelayCommand]
    private void ApplyToAll(StageCardViewModel source)
    {
        foreach (var card in Cards.Where(c => c != source))
        {
            card.ApplyFrom(source);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsStarting = true;
        Error = null;
        try
        {
            var project = await _client.CreateProjectAsync(BuildSetup());
            Name = "";
            Brief = "";
            ProjectStarted?.Invoke(this, project);
        }
        catch (Exception ex)
        {
            Error = ex is ArgumentException ? ex.Message : $"Could not start the project: {ex.Message}";
        }
        finally
        {
            IsStarting = false;
        }
    }

    private ProjectSetup BuildSetup() => new(
        Name,
        Brief,
        [.. ResearchSources],
        new ResearchSetup(Research.Provider, Research.Model, ResearchSlot.ToRef()!),
        new ScriptSetup(Script.Provider, Script.Model, Script.Slots[0].ToRef()!, StoryboardSlot.ToRef()!),
        new VoiceSetup(Voice.Provider, Voice.Model, Voice.Slots[0].ToRef()!),
        new ReferencesSetup(References.Provider, References.Model, References.Slots[0].ToRef()!, Consistency, ReviewReferenceList),
        new StillsSetup(Stills.Provider, Stills.Model, ImageSlot.ToRef()!, StillSize.Value!),
        new ClipsSetup(Clips.Provider, Clips.Model, VideoSlot.ToRef()!, MaxClip.Value!.Value, ClipSize.Value!),
        new OutputSetup(Aspect, SelectedDelivery.Value.Width, SelectedDelivery.Value.Height, TargetLength()!.Value, Language, Assembly),
        [.. RunPlan.Where(s => s.IsGated).Select(s => s.Stage)],
        StopAtGates ? RunMode.StopAtGates : RunMode.RunThrough,
        Pacing.Override);

    private int? TargetLength()
    {
        if (!IsCustomTarget)
        {
            return TargetSeconds;
        }
        // Checked in whole seconds, as stored: 0.005 minutes is no length at all.
        return double.TryParse(CustomTargetMinutes.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var minutes)
               && (int)Math.Round(minutes * 60) is var seconds and >= 1 and <= 36000
            ? seconds
            : null;
    }

    partial void OnAspectChanged(string value)
    {
        // The list first, then the pick in it: the same tier (768p, 1080p, 1440p) in the new aspect,
        // found by the short side, which every aspect's tier shares. The sizes follow it at once:
        // nothing to load, so Start never sees sizes for the old aspect.
        OnPropertyChanged(nameof(DeliveryChoices));
        var shortSide = Math.Min(SelectedDelivery.Value.Width, SelectedDelivery.Value.Height);
        SelectedDelivery = DeliveryChoices.FirstOrDefault(c => Math.Min(c.Value.Width, c.Value.Height) == shortSide) ?? DeliveryChoices[1];
        UpdateMissing();
    }

    partial void OnConsistencyChanged(Consistency value) => UpdateReferencesVia();

    /// <summary>The References row of the run plan names the model that makes the images, or that none are made.</summary>
    private string ReferencesVia() => Consistency == Consistency.TextToImageOnly
        ? "None: the stills are made from text only"
        : $"{References.Model} · gate required";

    private void UpdateReferencesVia() => RunPlan.Single(s => s.Stage == PipelineStage.References).Via = ReferencesVia();

    partial void OnNameChanged(string value) => UpdateMissing();

    partial void OnBriefChanged(string value) => UpdateMissing();

    partial void OnTargetSecondsChanged(int value) => UpdateMissing();

    partial void OnCustomTargetMinutesChanged(string value) => UpdateMissing();

    /// <summary>
    /// The research profile's sources. A new version of the same profile keeps the sources the
    /// user added; another profile starts from its own list.
    /// </summary>
    private void ApplyResearchProfile(ProfileContent? content, ProfileContent? previous, bool sameProfile)
    {
        var added = sameProfile
            ? ResearchSources.Except(previous?.ResearchSources ?? [], StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        ResearchSources.Clear();
        foreach (var source in (content?.ResearchSources ?? []).Concat(added).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ResearchSources.Add(source);
        }
    }

    /// <summary>A picked profile's defaults (sources, pacing, clip length) come from its content, which is loaded here.</summary>
    private void OnSlotChanged(PropertyChangedEventArgs e, ProfileSlotViewModel slot, Action<ProfileContent?, ProfileContent?, bool> apply)
    {
        if (e.PropertyName == nameof(ProfileSlotViewModel.Selected))
        {
            Track(ApplyContentAsync(slot, apply));
        }
    }

    /// <param name="apply">Gets the new content, the content applied before, and whether both are versions of the same profile.</param>
    private async Task ApplyContentAsync(ProfileSlotViewModel slot, Action<ProfileContent?, ProfileContent?, bool> apply)
    {
        var picked = slot.Selected;
        // The same profile announced again (the pickers are refilled each time the screen opens)
        // keeps what the user changed since: typed sources, a custom clip length or pacing.
        if (_applied.TryGetValue(slot, out var applied) && applied == picked)
        {
            return;
        }
        UpdateMissing();   // Start waits for these defaults
        var (loaded, content) = await LoadContentAsync(picked);
        // A failed load leaves everything as it was and is tried again next time; another
        // profile may also have been picked while this one loaded.
        if (loaded && slot.Selected == picked)
        {
            var sameProfile = applied is not null && picked is not null && applied.Id == picked.Id;
            var previous = _content.GetValueOrDefault(slot);
            _applied[slot] = picked;
            _content[slot] = content;
            apply(content, previous, sameProfile);
        }
        UpdateMissing();
    }

    private async Task<(bool Loaded, ProfileContent? Content)> LoadContentAsync(ProfileSummary? picked)
    {
        if (picked is null)
        {
            return (true, null);
        }
        try
        {
            return (true, (await _client.GetProfileVersionAsync(picked.Id, picked.LatestVersion)).Content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading profile {picked.Id} failed: {ex}");
            Error = $"Could not load {picked.Name}: {ex.Message}";
            return (false, null);
        }
    }

    private void Track(Task task)
    {
        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(task);
    }

    private void UpdateMissing()
    {
        var missing = new List<string>();
        if (Name.Trim().Length == 0)
        {
            missing.Add("a name");
        }
        if (Brief.Trim().Length == 0)
        {
            missing.Add("a brief");
        }
        missing.AddRange(Cards.SelectMany(c => c.Slots).Where(s => s.Selected is null).Select(s => $"a {s.Label.ToLowerInvariant()}"));
        // A profile's defaults (sources, pacing, clip length) must be in before Start, or the new
        // profile would be saved with the old one's values.
        missing.AddRange(new[] { ResearchSlot, StoryboardSlot, VideoSlot }
            .Where(s => s.Selected is not null && _applied.GetValueOrDefault(s) != s.Selected)
            .Select(s => $"the defaults of {s.Selected!.Name} (loading, or they could not be read)"));
        if (StillSize.Problem is { } still)
        {
            missing.Add($"{still} for the still images");
        }
        if (ClipSize.Problem is { } clip)
        {
            missing.Add($"{clip} for the video clips");
        }
        if (MaxClip.Value is null)
        {
            missing.Add("a valid max clip length");
        }
        if (TargetLength() is null)
        {
            missing.Add("a valid target length");
        }
        if (PacingProblem is not null)
        {
            missing.Add("a valid pacing");
        }
        OnPropertyChanged(nameof(PacingProblem));
        OnPropertyChanged(nameof(ShotEstimate));
        MissingText = missing.Count == 0 ? null : $"Needs {JoinAnd(missing)}.";
        OnPropertyChanged(nameof(MissingText));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static string JoinAnd(List<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items[..^1])} and {items[^1]}";

    private static Choice<GenerationSize> Delivery(string aspect, int width, int height) =>
        new(new GenerationSize(aspect, width, height), $"{width} × {height}");
}
