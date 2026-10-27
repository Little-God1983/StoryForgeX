using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.References;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Stills;

/// <summary>
/// Test render on the Profiles screen: an image profile, saved or not, makes one picture from a test
/// line in its prompt template, at its 16:9 size (else its first). It goes into no project: the
/// picture is kept in the engine's data folder.
/// </summary>
internal sealed class TestRenders(IComfyUi comfy, SettingsStore settings, IOptions<StoryForgeEngineOptions> options, TimeProvider clock)
{
    /// <summary>What a profile with no size of its own is tested at.</summary>
    private static readonly GenerationSize Square = new("1:1", 1024, 1024);

    /// <exception cref="ArgumentException">The test line is empty.</exception>
    /// <exception cref="InvalidOperationException">The profile cannot make a picture, or ComfyUI could not: the reason in plain words.</exception>
    public async Task<TestRender> RenderAsync(ProfileContent profile, string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new ArgumentException("Type what the test picture shows first.", nameof(line));
        }
        var started = clock.GetTimestamp();
        var size = profile.Sizes.FirstOrDefault(s => s.Aspect == "16:9") ?? profile.Sizes.FirstOrDefault() ?? Square;
        var prompt = StillsStage.Compose(profile.PromptTemplate, line.Trim(), []);
        var seed = Random.Shared.Next();
        var folder = Path.Combine(options.Value.DataDirectory, "test-renders");
        try
        {
            if (!string.Equals(profile.Provider, VoiceStage.ComfyUi, StringComparison.OrdinalIgnoreCase))
            {
                throw new StageFailedException($"Test render works through ComfyUI for now, and this profile uses {(profile.Provider.Length == 0 ? "no provider" : profile.Provider)}.");
            }
            var templates = (await settings.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
            var workflow = await WorkflowTemplate.LoadAsync(templates, profile.WorkflowTemplate, cancellationToken, "image");
            if (!WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.PromptKey))
            {
                throw new StageFailedException($"The profile does not say where the prompt goes. Map '{ReferencesStage.PromptKey}' to the prompt node's text.");
            }
            var values = new Dictionary<string, JsonNode>
            {
                [ReferencesStage.PromptKey] = prompt,
                [ReferencesStage.SeedKey] = seed,
                [ReferencesStage.WidthKey] = size.Width,
                [ReferencesStage.HeightKey] = size.Height,
            };
            if (profile.NegativePrompt.Trim() is { Length: > 0 } negative)
            {
                values[StillsStage.NegativeKey] = negative;
            }
            if (profile.Steps is { } steps)
            {
                values[StillsStage.StepsKey] = steps;
            }
            if (WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.ImageKey) || WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.ImagesKey))
            {
                // An edit workflow paints on a blank canvas, not on the picture it was saved with.
                var blank = Path.Combine(folder, $"blank-{size.Width}x{size.Height}.png");
                if (!File.Exists(blank))
                {
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(blank, BlankCanvas.Png(size.Width, size.Height), cancellationToken);
                }
                var uploaded = await comfy.UploadAsync(blank, cancellationToken);
                values[ReferencesStage.ImageKey] = uploaded;
                values[ReferencesStage.ImagesKey] = JsonSerializer.Serialize(new[] { new { name = uploaded, subfolder = "", type = "input", enabled = true } });
            }
            var files = await comfy.RunAsync(
                WorkflowTemplate.Apply(workflow, profile.Inputs, values),
                "StoryForge · test render",
                new Progress<string>(),
                cancellationToken);
            var picture = files.FirstOrDefault(f => f.Kind == "images" && Path.GetExtension(f.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                ?? throw new StageFailedException("The workflow saved no image. It needs a node that saves it, e.g. Save Image.");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(picture.Name)}");
            await File.WriteAllBytesAsync(path, picture.Content, cancellationToken);
            return new TestRender(path, prompt, seed, size, clock.GetElapsedTime(started));
        }
        catch (StageFailedException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }
}
