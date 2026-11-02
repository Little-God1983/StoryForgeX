using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>The workflows that come with the app, in StoryForge.App/Workflows: loaded and filled as a profile does.</summary>
public sealed class ShippedWorkflowTests
{
    private static string Folder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StoryForgeX.slnx")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("StoryForgeX.slnx"), "StoryForge.App", "Workflows");
    }

    /// <summary>Where the Qwen image-edit workflow takes what the References stage gives it.</summary>
    public static IReadOnlyList<WorkflowInput> QwenInputs { get; } =
        [new("prompt", "#16.value"), new("images", "#10.files_json"), new("seed", "#5.seed"), new("width", "#8.width"), new("height", "#8.height")];

    [Fact]
    public async Task The_Qwen_image_edit_workflow_takes_the_prompt_the_pictures_the_seed_and_the_size()
    {
        var workflow = await WorkflowTemplate.LoadAsync(Folder(), "Qwen2-1 Image-edit (API).json", CancellationToken.None, "image");

        var filled = WorkflowTemplate.Apply(workflow, QwenInputs, new Dictionary<string, JsonNode>
        {
            ["prompt"] = "A clean reference picture of Karlach",
            ["images"] = """[{"name":"storyforge-ab.png","subfolder":"","type":"input","enabled":true}]""",
            ["seed"] = 7,
            ["width"] = 1024,
            ["height"] = 1024,
        });

        Assert.Equal("A clean reference picture of Karlach", (string)filled["16"]!["inputs"]!["value"]!);
        Assert.Contains("storyforge-ab.png", (string)filled["10"]!["inputs"]!["files_json"]!);
        Assert.Equal((7, 1024, 1024), ((int)filled["5"]!["inputs"]!["seed"]!, (int)filled["8"]!["inputs"]!["width"]!, (int)filled["8"]!["inputs"]!["height"]!));
        // One node saves a picture, so the one the app keeps is the one made.
        Assert.Equal(["SaveImageAdvanced"], filled.Select(n => (string)n.Value!["class_type"]!).Where(t => t.Contains("Save", StringComparison.Ordinal) || t.Contains("Compare", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_Breeze_workflow_lets_go_of_Whisper_at_once_and_of_Breeze_after_the_last_part()
    {
        var workflow = await WorkflowTemplate.LoadAsync(Folder(), "BreezeTTS - 3. Voice Direction by Into the Latent.json", CancellationToken.None);
        IReadOnlyList<WorkflowInput> inputs = [new(VoiceStage.TextKey, "#11.text")];
        static bool Unloads(JsonObject w, string node) => (bool)w[node]!["inputs"]!["unload_after"]!;

        // Plain switches, or a new export would quietly leave the voice models on the graphics card (#80).
        Assert.True(WorkflowTemplate.SetUnloadAfter(workflow, inputs, VoiceStage.TextKey, last: false));
        Assert.Equal((false, true), (Unloads(workflow, "11"), Unloads(workflow, "19")));

        Assert.True(WorkflowTemplate.SetUnloadAfter(workflow, inputs, VoiceStage.TextKey, last: true));
        Assert.Equal((true, true), (Unloads(workflow, "11"), Unloads(workflow, "19")));
    }
}
