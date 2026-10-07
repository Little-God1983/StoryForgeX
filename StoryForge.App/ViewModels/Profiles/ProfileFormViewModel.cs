using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Profiles;

/// <summary>
/// The fields of one profile version, as the editor shows them. Which fields appear depends on the
/// kind; the ones a kind does not show are kept as loaded, so saving never drops them.
/// </summary>
public sealed partial class ProfileFormViewModel : ObservableValidator
{
    // HasErrors is not a field but does count as a change: the editor's Save button depends on it.
    private static readonly HashSet<string> NotFields = [nameof(TemplateChoices), nameof(HasTemplates)];

    private ProfileContent _loaded;
    private IReadOnlyList<string> _templates = [];
    private bool _loading;

    public ProfileFormViewModel(ProfileKind kind, ProfileContent content, bool isReadOnly)
    {
        Kind = kind;
        IsReadOnly = isReadOnly;
        _loaded = content;
        Inputs.CollectionChanged += OnRowsChanged;
        Sizes.CollectionChanged += OnRowsChanged;
        ReferenceFiles.CollectionChanged += (_, _) => RaiseChanged();
        Load(content);
    }

    public ProfileKind Kind { get; }

    /// <summary>An older version: shown as saved, never edited.</summary>
    public bool IsReadOnly { get; }

    public bool IsEditable => !IsReadOnly;

    public bool IsResearch => Kind == ProfileKind.Research;

    public bool IsMedia => Kind is ProfileKind.Image or ProfileKind.Video or ProfileKind.Voice;

    public bool HasNegativePrompt => Kind is ProfileKind.Image or ProfileKind.Video;

    public bool HasSizes => Kind is ProfileKind.Image or ProfileKind.Video;

    public bool HasSteps => Kind is ProfileKind.Image or ProfileKind.Video;

    public bool IsVideo => Kind == ProfileKind.Video;

    public bool IsVoice => Kind == ProfileKind.Voice;

    public string ReferenceFilesTitle => IsVoice ? "Reference audio" : "Reference images";

    public string InstructionsLabel => Kind switch
    {
        ProfileKind.Research => "Instructions for the LLM when it researches",
        ProfileKind.Script => "Instructions for the LLM when it writes the script",
        ProfileKind.Storyboard => "Instructions for the LLM when it plans the shots",
        _ => "Instructions for the LLM when it writes prompts for this model",
    };

    private static readonly IReadOnlyList<string> MediaProviders = ["ComfyUI"];
    private static readonly IReadOnlyList<string> TextProviders = ["Claude CLI", "LM Studio"];

    public IReadOnlyList<string> ProviderChoices => IsMedia ? MediaProviders : TextProviders;

    [ObservableProperty]
    private string _instructions = "";

    /// <summary>Research only: one source per line.</summary>
    [ObservableProperty]
    private string _researchSources = "";

    [ObservableProperty]
    private string _provider = "";

    [ObservableProperty]
    private string _workflowTemplate = "";

    [ObservableProperty]
    private string _promptTemplate = "";

    [ObservableProperty]
    private string _negativePrompt = "";

    /// <summary>Empty: the workflow's own value.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 1000, ErrorMessage = "Between 1 and 1000, or empty.")]
    private int? _steps;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 600, ErrorMessage = "Between 1 and 600 seconds.")]
    private int? _maxClipSeconds;

    [ObservableProperty]
    private string _voice = "";

    public ObservableCollection<WorkflowInputRowViewModel> Inputs { get; } = [];

    public ObservableCollection<SizeRowViewModel> Sizes { get; } = [];

    /// <summary>As stored by the engine; see <see cref="IStoryForgeClient.ImportReferenceFileAsync"/>.</summary>
    public ObservableCollection<string> ReferenceFiles { get; } = [];

    /// <summary>The workflow files to pick from, plus the saved one if it has gone from the folder.</summary>
    public IReadOnlyList<string> TemplateChoices { get; private set; } = [];

    public bool HasTemplates { get; private set; }

    /// <summary>Raised after any field, row or reference file changed (not while loading).</summary>
    public event EventHandler? Changed;

    public bool HasAnyErrors => HasErrors || Sizes.Any(s => s.HasErrors);

    public void SetTemplates(IReadOnlyList<string> templates)
    {
        _templates = templates;
        HasTemplates = templates.Count > 0;
        TemplateChoices = WorkflowTemplate.Length == 0 || templates.Contains(WorkflowTemplate)
            ? templates
            : [WorkflowTemplate, .. templates];
        OnPropertyChanged(nameof(TemplateChoices));
        OnPropertyChanged(nameof(HasTemplates));
    }

    /// <summary>Replaces every field with <paramref name="content"/>; raises <see cref="Changed"/> once.</summary>
    public void Load(ProfileContent content)
    {
        _loading = true;
        try
        {
            _loaded = content;
            Instructions = content.Instructions;
            ResearchSources = string.Join(Environment.NewLine, content.ResearchSources);
            Provider = content.Provider;
            WorkflowTemplate = content.WorkflowTemplate;
            PromptTemplate = content.PromptTemplate;
            NegativePrompt = content.NegativePrompt;
            Steps = content.Steps;
            MaxClipSeconds = content.MaxClipSeconds;
            Voice = content.Voice;
            Replace(Inputs, content.Inputs.Select(i => new WorkflowInputRowViewModel(i.Key, i.Node, IsReadOnly)));
            Replace(Sizes, content.Sizes.Select(s => new SizeRowViewModel(s.Aspect, s.Width, s.Height, IsReadOnly)));
            Replace(ReferenceFiles, content.ReferenceFiles);
            ValidateAllProperties();
        }
        finally
        {
            _loading = false;
        }
        SetTemplates(_templates);
        RaiseChanged();
    }

    public ProfileContent ToContent() => _loaded with
    {
        Instructions = Instructions,
        ResearchSources = [.. ResearchSources.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
        Provider = Provider,
        WorkflowTemplate = WorkflowTemplate,
        PromptTemplate = PromptTemplate,
        NegativePrompt = NegativePrompt,
        Inputs = [.. Inputs.Select(i => new WorkflowInput(i.Key, i.Node.Trim()))],
        Steps = Steps,
        Sizes = [.. Sizes.Select(s => new GenerationSize(s.Aspect, s.Width, s.Height))],
        ReferenceFiles = [.. ReferenceFiles],
        MaxClipSeconds = MaxClipSeconds,
        Voice = Voice,
    };

    /// <summary>Records compare lists by reference; two contents are the same when they serialize the same.</summary>
    public static bool SameContent(ProfileContent a, ProfileContent b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && !NotFields.Contains(name))
        {
            RaiseChanged();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
        {
            row.PropertyChanged += (_, _) => RaiseChanged();
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}

/// <summary>Which workflow node one profile value goes into, e.g. prompt → #6.text.</summary>
public sealed partial class WorkflowInputRowViewModel(string key, string node, bool isReadOnly) : ObservableObject
{
    public string Key { get; } = key;

    public bool IsReadOnly { get; } = isReadOnly;

    [ObservableProperty]
    private string _node = node;
}

/// <summary>The generation size for one aspect, e.g. 16:9 → 1344 × 768.</summary>
public sealed partial class SizeRowViewModel : ObservableValidator
{
    public SizeRowViewModel(string aspect, int width, int height, bool isReadOnly)
    {
        Aspect = aspect;
        IsReadOnly = isReadOnly;
        _width = width;
        _height = height;
        ValidateAllProperties();
    }

    public string Aspect { get; }

    public bool IsReadOnly { get; }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 16384, ErrorMessage = "1 to 16384.")]
    private int _width;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 16384, ErrorMessage = "1 to 16384.")]
    private int _height;
}
