namespace StoryForge.Client;

/// <summary>Everything the Settings screen edits. Secrets are not part of it; see <see cref="SecretKey"/>.</summary>
public sealed record EngineSettings(
    ClaudeCliSettings ClaudeCli,
    LmStudioSettings LmStudio,
    ComfyUiSettings ComfyUi,
    FfmpegSettings Ffmpeg,
    PathSettings Paths)
{
    public static EngineSettings Defaults { get; } = new(
        new ClaudeCliSettings("claude", "-p --output-format json", TimeoutSeconds: 600, MaxParallel: 2),
        new LmStudioSettings("http://localhost:1234/v1", Model: "", StructuredOutputMode.JsonSchema, WebResearchMode.ViaResearchProvider),
        new ComfyUiSettings("127.0.0.1", Port: 8188, WorkflowTemplatesFolder: "", GpuSlots: 1, FreeVramBetweenStages: true),
        new FfmpegSettings("ffmpeg", TimelineExport.Fcpxml),
        new PathSettings(ProjectsFolder: ""));
}

/// <param name="Executable">A path, or a name found on PATH (e.g. "claude").</param>
public sealed record ClaudeCliSettings(string Executable, string Arguments, int TimeoutSeconds, int MaxParallel);

public enum StructuredOutputMode
{
    JsonSchema,
    PromptOnlyValidate,
}

public enum WebResearchMode
{
    ViaResearchProvider,
    None,
}

/// <param name="BaseUrl">The OpenAI-compatible endpoint, including /v1.</param>
/// <param name="Model">The model id to use; empty means "whatever LM Studio has loaded".</param>
public sealed record LmStudioSettings(string BaseUrl, string Model, StructuredOutputMode StructuredOutput, WebResearchMode WebResearch);

/// <param name="WorkflowTemplatesFolder">Where the API-format workflow templates live (used from #3 on).</param>
public sealed record ComfyUiSettings(string Host, int Port, string WorkflowTemplatesFolder, int GpuSlots, bool FreeVramBetweenStages);

public enum TimelineExport
{
    Fcpxml,
    OpenTimelineIo,
    None,
}

public sealed record FfmpegSettings(string Executable, TimelineExport TimelineExport);

/// <param name="ProjectsFolder">Where project media goes; empty means the engine's default folder.</param>
public sealed record PathSettings(string ProjectsFolder);

/// <summary>Secrets the engine keeps in the OS credential store, never in settings.</summary>
public enum SecretKey
{
    LmStudioApiToken,
}
