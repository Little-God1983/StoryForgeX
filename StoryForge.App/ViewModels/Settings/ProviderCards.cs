using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Settings;

public sealed partial class ClaudeCliCardViewModel() : ProviderCardViewModel(ProviderId.ClaudeCli, "Claude CLI", "LLM · CLI process")
{
    [ObservableProperty]
    private string _executable = "";

    [ObservableProperty]
    private string _arguments = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 86400, ErrorMessage = "Between 1 and 86400 seconds.")]
    private int _timeoutSeconds;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 64, ErrorMessage = "Between 1 and 64.")]
    private int _maxParallel;

    protected override void LoadFields(EngineSettings settings)
    {
        Executable = settings.ClaudeCli.Executable;
        Arguments = settings.ClaudeCli.Arguments;
        TimeoutSeconds = settings.ClaudeCli.TimeoutSeconds;
        MaxParallel = settings.ClaudeCli.MaxParallel;
    }

    public override EngineSettings ApplyTo(EngineSettings settings) =>
        settings with { ClaudeCli = new ClaudeCliSettings(Executable.Trim(), Arguments.Trim(), TimeoutSeconds, MaxParallel) };
}

public sealed partial class LmStudioCardViewModel(IStoryForgeClient client)
    : ProviderCardViewModel(ProviderId.LmStudio, "LM Studio", "LLM · OpenAI-compatible HTTP")
{
    public static IReadOnlyList<Choice<StructuredOutputMode>> StructuredOutputChoices { get; } =
    [
        new(StructuredOutputMode.JsonSchema, "JSON schema"),
        new(StructuredOutputMode.PromptOnlyValidate, "Prompt-only + validate"),
    ];

    public static IReadOnlyList<Choice<WebResearchMode>> WebResearchChoices { get; } =
    [
        new(WebResearchMode.ViaResearchProvider, "Via research provider"),
        new(WebResearchMode.None, "None"),
    ];

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _model = "";

    [ObservableProperty]
    private StructuredOutputMode _structuredOutput;

    [ObservableProperty]
    private WebResearchMode _webResearch;

    /// <summary>Whether an API token is stored. The token itself never comes back to the app.</summary>
    public bool HasApiToken { get; private set; }

    public async Task LoadApiTokenStateAsync() => SetHasApiToken(await client.HasSecretAsync(SecretKey.LmStudioApiToken));

    /// <summary>Raised after the token was stored or removed (not when its state is first read).</summary>
    public event EventHandler? ApiTokenChanged;

    /// <summary>Stores the token, trimmed (pasted tokens often carry a newline); a blank one is ignored.</summary>
    public async Task SaveApiTokenAsync(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            return;
        }
        await client.SetSecretAsync(SecretKey.LmStudioApiToken, token);
        SetHasApiToken(true);
        ApiTokenChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveApiTokenAsync()
    {
        await client.SetSecretAsync(SecretKey.LmStudioApiToken, null);
        SetHasApiToken(false);
        ApiTokenChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHasApiToken(bool value)
    {
        HasApiToken = value;
        OnPropertyChanged(nameof(HasApiToken));
    }

    // The token lives in the credential store, not in settings: changing it is not a settings edit.
    protected override bool IsNotAField(string propertyName) =>
        propertyName == nameof(HasApiToken) || base.IsNotAField(propertyName);

    protected override void LoadFields(EngineSettings settings)
    {
        BaseUrl = settings.LmStudio.BaseUrl;
        Model = settings.LmStudio.Model;
        StructuredOutput = settings.LmStudio.StructuredOutput;
        WebResearch = settings.LmStudio.WebResearch;
    }

    public override EngineSettings ApplyTo(EngineSettings settings) =>
        settings with { LmStudio = new LmStudioSettings(BaseUrl.Trim(), Model.Trim(), StructuredOutput, WebResearch) };
}

public sealed partial class ComfyUiCardViewModel() : ProviderCardViewModel(ProviderId.ComfyUi, "ComfyUI", "Image · Video · TTS")
{
    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "Between 1 and 65535.")]
    private int _port;

    [ObservableProperty]
    private string _workflowTemplatesFolder = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 16, ErrorMessage = "Between 1 and 16.")]
    private int _gpuSlots;

    [ObservableProperty]
    private bool _freeVramBetweenStages;

    protected override void LoadFields(EngineSettings settings)
    {
        Host = settings.ComfyUi.Host;
        Port = settings.ComfyUi.Port;
        WorkflowTemplatesFolder = settings.ComfyUi.WorkflowTemplatesFolder;
        GpuSlots = settings.ComfyUi.GpuSlots;
        FreeVramBetweenStages = settings.ComfyUi.FreeVramBetweenStages;
    }

    public override EngineSettings ApplyTo(EngineSettings settings) =>
        settings with { ComfyUi = new ComfyUiSettings(Host.Trim(), Port, WorkflowTemplatesFolder.Trim(), GpuSlots, FreeVramBetweenStages) };
}

public sealed partial class FfmpegCardViewModel() : ProviderCardViewModel(ProviderId.Ffmpeg, "FFmpeg", "Assembly · headless")
{
    public static IReadOnlyList<Choice<TimelineExport>> TimelineExportChoices { get; } =
    [
        new(TimelineExport.Fcpxml, "FCPXML"),
        new(TimelineExport.OpenTimelineIo, "OpenTimelineIO"),
        new(TimelineExport.None, "None"),
    ];

    [ObservableProperty]
    private string _executable = "";

    [ObservableProperty]
    private TimelineExport _timelineExport;

    protected override void LoadFields(EngineSettings settings)
    {
        Executable = settings.Ffmpeg.Executable;
        TimelineExport = settings.Ffmpeg.TimelineExport;
    }

    public override EngineSettings ApplyTo(EngineSettings settings) =>
        settings with { Ffmpeg = new FfmpegSettings(Executable.Trim(), TimelineExport) };
}
