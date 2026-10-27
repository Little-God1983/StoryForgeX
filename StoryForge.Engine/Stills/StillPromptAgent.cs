using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;

namespace StoryForge.Engine.Stills;

/// <summary>A picture a still is made from, as the prompt names it: "picture 1 shows Karlach".</summary>
/// <param name="Look">What it looks like, in words; empty when unknown.</param>
internal sealed record StillPicture(string Name, string Look);

/// <summary>A shot as the prompt writer sees it: what the storyboard says, what is heard, what it starts from.</summary>
/// <param name="Label">"S01·2".</param>
/// <param name="Pictures">The pictures the image model gets with the prompt, in order; empty for text only.</param>
/// <param name="FromShotBefore">The first picture is the still of the shot before: the camera moves on in the same scene.</param>
/// <param name="Described">Who is in it with a reference but no picture (the workflow takes one): in words only.</param>
internal sealed record StillShot(
    string Label, string Description, IReadOnlyList<string> Cast, string Heard, IReadOnlyList<StillPicture> Pictures, bool FromShotBefore, IReadOnlyList<StillPicture> Described);

/// <summary>What the still prompts are written from: the brief, the shots, and the image profile's instructions.</summary>
/// <param name="Model">The model to use; empty means the provider's default.</param>
internal sealed record StillPromptRequest(Guid ProjectId, string Brief, IReadOnlyList<StillShot> Shots, string Instructions, string Model);

/// <summary>The model that writes one image prompt per shot. It has no tools: the shots in the prompt are all it knows.</summary>
internal interface IStillPromptAgent
{
    /// <summary>A prompt for every shot asked for; with a session and problems, the same conversation fixes its last answer.</summary>
    /// <exception cref="Pipeline.StageFailedException">The model could not be asked.</exception>
    Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(
        StillPromptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken);
}

// What the model hands back. The schema Claude CLI enforces is generated from these records; its
// text stays plain ASCII, because npm's claude.cmd passes it through cmd.exe.

internal sealed record StillPromptsOutput(
    [property: Description("One prompt for every shot asked for, in the order asked.")] IReadOnlyList<StillPromptOutput> Prompts);

internal sealed record StillPromptOutput(
    [property: Description("The shot, as the prompt names it, e.g. S01-2.")] string Shot,
    [property: Description("The image prompt for this shot: what is seen, written for the image model.")] string Prompt);

/// <summary>Checks the model's prompts: one for every shot asked for, none empty, none for a shot not asked for.</summary>
internal static class StillPromptCheck
{
    public static IReadOnlyList<string> Problems(StillPromptsOutput? output, IReadOnlyList<StillShot> shots)
    {
        if (output?.Prompts is null)
        {
            return ["The answer had no prompts."];
        }
        var problems = new List<string>();
        var answered = output.Prompts.Where(p => p is not null).ToList();
        foreach (var extra in answered.Where(p => shots.All(s => !Same(s.Label, p.Shot))))
        {
            problems.Add($"{extra.Shot} is not one of the shots asked for.");
        }
        foreach (var shot in shots)
        {
            var mine = answered.Where(p => Same(shot.Label, p.Shot)).ToList();
            if (mine.Count == 0)
            {
                problems.Add($"{Name(shot.Label)} has no prompt.");
            }
            else if (mine.Count > 1)
            {
                problems.Add($"{Name(shot.Label)} has {mine.Count} prompts; give it one.");
            }
            else if (string.IsNullOrWhiteSpace(mine[0].Prompt))
            {
                problems.Add($"The prompt for {Name(shot.Label)} is empty.");
            }
        }
        return problems;
    }

    /// <summary>The prompt of each shot, by label; only for an answer with no problems.</summary>
    public static IReadOnlyDictionary<string, string> ByLabel(StillPromptsOutput output, IReadOnlyList<StillShot> shots) =>
        shots.ToDictionary(s => s.Label, s => output.Prompts.First(p => p is not null && Same(s.Label, p.Shot)).Prompt.Trim());

    /// <summary>"S01-2": the label as the model writes it, in ASCII.</summary>
    public static string Name(string label) => label.Replace('·', '-');

    private static bool Same(string label, string? answered) =>
        answered is not null && string.Equals(Name(label), Name(answered.Trim()), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The still prompts through Claude CLI, with no tools and no MCP servers at all.</summary>
internal sealed class ClaudeCliStillPromptAgent(ClaudeCli claude, IOptions<StoryForgeEngineOptions> options, TimeProvider clock) : IStillPromptAgent
{
    internal const string SystemPrompt =
        """
        You write the image prompts of StoryForge X, which makes narrated videos. Every shot of the
        storyboard becomes one still picture, made by an image model from your prompt and from the
        pictures it is given of who and what is in the shot. You describe what is seen; you never ask
        for text, letters or logos in the picture.
        """;

    internal static string Schema { get; } = OutputSchema.Json<StillPromptsOutput>();

    public async Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(
        StillPromptRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
    {
        var what = request.Shots.Count == 1 ? $"the prompt for {request.Shots[0].Label}" : $"the prompts for {request.Shots.Count} shots";
        Report(activity, session is null ? $"writing {what}" : $"fixing {what}");
        var reply = await claude.AskAsync(
            new ClaudeCall(
                // One folder per project: a correction resumes the session, and Claude CLI finds sessions by folder.
                Path.Combine(options.Value.DataDirectory, "stills", request.ProjectId.ToString("N")),
                SystemPrompt,
                session is null ? FirstPrompt(request) : ClaudeCliScriptAgent.CorrectionPrompt(problems, "every prompt"),
                Schema,
                request.Model,
                session,
                McpConfig: null,
                AllowedTools: [],
                "the still prompts"),
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

    internal static string FirstPrompt(StillPromptRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Write one image prompt for each of these shots.");
        prompt.AppendLine();
        prompt.AppendLine("Brief:");
        prompt.AppendLine(request.Brief);
        foreach (var shot in request.Shots)
        {
            prompt.AppendLine();
            prompt.AppendLine(CultureInfo.InvariantCulture, $"{StillPromptCheck.Name(shot.Label)}: {shot.Description}");
            if (shot.Heard.Length > 0)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"Heard meanwhile: \"{shot.Heard}\"");
            }
            if (shot.Cast.Count > 0)
            {
                prompt.AppendLine($"In it: {string.Join(", ", shot.Cast)}");
            }
            for (var i = 0; i < shot.Pictures.Count; i++)
            {
                var picture = shot.Pictures[i];
                var shows = i == 0 && shot.FromShotBefore ? "the still of the shot before (same scene)" : picture.Name;
                prompt.AppendLine(CultureInfo.InvariantCulture, $"Picture {i + 1} shows {shows}{(picture.Look.Length > 0 ? $": {picture.Look}" : "")}");
            }
            foreach (var described in shot.Described)
            {
                prompt.AppendLine(CultureInfo.InvariantCulture, $"In no picture, so describe how they look: {described.Name}{(described.Look.Length > 0 ? $": {described.Look}" : "")}");
            }
        }
        prompt.AppendLine();
        prompt.AppendLine(
            """
            How to write them:
            1. One prompt per shot, for the shot as the storyboard describes it: subject, setting, light, camera.
            2. When a shot comes with pictures, the image model sees them: name who is in which picture ("the woman from picture 1") and say what they do in this shot. Do not describe their looks again in other words.
            3. A shot that starts from the still of the shot before keeps that scene: say how the camera moves on and what changes.
            4. Never ask for text, letters, captions or logos in the picture.
            """);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("From the image profile:");
            prompt.AppendLine(request.Instructions.Trim());
        }
        return prompt.ToString();
    }

    private void Report(IProgress<ActivityLine> activity, string text) =>
        activity.Report(new ActivityLine(clock.GetUtcNow(), ActivityKind.Model, text));
}
