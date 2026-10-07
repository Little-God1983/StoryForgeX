namespace StoryForge.Client;

/// <summary>The stage a profile is a recipe for, in the order the Profiles screen groups them.</summary>
public enum ProfileKind
{
    Research,
    Script,
    Storyboard,
    Image,
    Video,
    Voice,
}

/// <summary>One profile in the list: "Painted dark fantasy · v3".</summary>
public sealed record ProfileSummary(Guid Id, ProfileKind Kind, string Name, int LatestVersion);

/// <summary>One saved version of a profile. A version is never changed once saved.</summary>
public sealed record ProfileVersion(Guid ProfileId, int Version, DateTimeOffset SavedAt, ProfileContent Content);

/// <summary>A workflow node a profile value goes into, e.g. Key "prompt" → Node "#6.text".</summary>
public sealed record WorkflowInput(string Key, string Node);

/// <summary>The generation size for one aspect, e.g. "16:9" → 1344 × 768.</summary>
public sealed record GenerationSize(string Aspect, int Width, int Height);

/// <summary>
/// Everything a profile can hold. One shape for every kind; a kind leaves the fields it does not
/// use at their defaults (a script profile has no workflow, a research profile no prompt template).
/// </summary>
/// <param name="Instructions">For the LLM: how to write the script, the shots, or the prompts for this model.</param>
/// <param name="ResearchSources">Research only: the default sources a project starts with.</param>
/// <param name="WorkflowTemplate">Media only: the API-format workflow file in the ComfyUI templates folder.</param>
/// <param name="PromptTemplate">Media only: e.g. "{shot.visual}, {characters.sheet}, painted dark fantasy".</param>
/// <param name="Inputs">Media only: which value goes into which workflow node.</param>
/// <param name="Sizes">Image and video: the generation size per aspect.</param>
/// <param name="ReferenceFiles">Media only: reference images (or reference audio for a voice), as stored by the engine.</param>
/// <param name="MaxClipSeconds">Video only: the longest clip the model makes.</param>
/// <param name="Voice">Voice only: the voice the TTS workflow uses.</param>
public sealed record ProfileContent(
    string Instructions,
    IReadOnlyList<string> ResearchSources,
    string Provider,
    string WorkflowTemplate,
    string PromptTemplate,
    string NegativePrompt,
    IReadOnlyList<WorkflowInput> Inputs,
    int? Steps,
    IReadOnlyList<GenerationSize> Sizes,
    IReadOnlyList<string> ReferenceFiles,
    int? MaxClipSeconds,
    string Voice)
{
    /// <summary>Every text empty, every list empty, no numbers set.</summary>
    public static ProfileContent Empty { get; } = new("", [], "", "", "", "", [], null, [], [], null, "");
}
