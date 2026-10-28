using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Clips;

/// <summary>A shot as the motion prompt writer sees it: what the storyboard says, what is heard, what its still shows.</summary>
/// <param name="Label">"S01·2".</param>
/// <param name="Still">The prompt its still was made from: what the first frame shows.</param>
/// <param name="Seconds">How long the clip runs.</param>
internal sealed record ClipShot(string Label, string Description, string Heard, string Still, double Seconds);

/// <summary>What the motion prompts are written from: the brief, the shots, and the video profile's instructions.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record ClipPromptRequest(Guid ProjectId, string Brief, IReadOnlyList<ClipShot> Shots, string Instructions, string Model);

/// <summary>The model that writes one motion prompt per shot. It has no tools: the shots in the prompt are all it knows.</summary>
internal interface IClipPromptAgent
{
    /// <summary>A prompt for every shot asked for (by label, as the still prompts are); with a session and problems, the same conversation fixes its last answer.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(
        ClipPromptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

/// <summary>The motion prompts through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliClipPromptAgent(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IClipPromptAgent
{
    internal const string SystemPrompt =
        """
        You write the motion prompts of StoryForge X, which makes narrated videos. Every shot of the
        storyboard becomes one video clip, made by a video model that starts from the shot's still
        picture and follows your prompt. The still already sets the look: you say what moves and how
        the camera moves. You never ask for text, letters or logos.
        """;

    internal static string Schema { get; } = OutputSchema.Json<StillPromptsOutput>();

    public async Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(
        ClipPromptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        var what = request.Shots.Count == 1 ? $"the motion prompt for {request.Shots[0].Label}" : $"the motion prompts for {request.Shots.Count} shots";
        Report(activity, session is null ? $"writing {what}" : $"fixing {what}");
        var reply = await claude.AskAsync(
            new ClaudeCall(
                // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
                Path.Combine(options.Value.DataDirectory, "clips", request.ProjectId.ToString("N")),
                SystemPrompt,
                session is null ? FirstPrompt(request) : ClaudeCliScriptAgent.CorrectionPrompt(problems, "every prompt"),
                Schema,
                request.Model,
                session,
                McpConfig: null,
                AllowedTools: [],
                "the motion prompts"),
            new ClaudeStream(new ResearchPages(), activity, clock, $"writing {what}"),
            cancellationToken);
        if (reply.CostUsd is { } cost)
        {
            Report(activity, string.Create(CultureInfo.InvariantCulture, $"answer received · ${cost:0.00}"));
        }
        StillPromptsOutput? output;
        try
        {
            output = OutputSchema.Read<StillPromptsOutput>(reply.Output);
        }
        catch (JsonException)
        {
            output = null;   // reported as "no prompts", and the model gets to try again
        }
        return new ScriptAnswer<StillPromptsOutput>(reply.Session, output);
    }

    internal static string FirstPrompt(ClipPromptRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Write one motion prompt for each of these shots.");
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        foreach (var shot in request.Shots)
        {
            prompt.AppendLine();
            prompt.AppendLine(CultureInfo.InvariantCulture, $"{StillPromptCheck.Name(shot.Label)} ({shot.Seconds:0.#} s): {shot.Description}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"Its still shows: {shot.Still}");
            if (shot.Heard.Length > 0)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"Heard meanwhile: \"{shot.Heard}\"");
            }
        }
        prompt.AppendLine();
        prompt.AppendLine(
            """
            How to write them:
            1. One prompt per shot. The clip starts from the still: describe what moves in it and how the camera moves, for as long as the clip runs.
            2. Keep to what the still shows; do not bring in people or things that are not in it.
            3. One clear motion that fits the length: a slow push-in, a pan, a character turning, smoke drifting. No cuts within a clip.
            4. Never ask for text, letters, captions or logos.
            """);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("From the video profile:");
            prompt.AppendLine(request.Instructions.Trim());
        }
        return prompt.ToString();
    }

    private void Report(IProgress<ActivityLine> activity, string text) =>
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, text));
}
