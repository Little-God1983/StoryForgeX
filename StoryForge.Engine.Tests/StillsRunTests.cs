using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.References;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Stills stage in the pipeline, with stand-in prompts and ComfyUI: it starts when the
/// references are approved, makes one still per shot from the reference images of who is in it
/// (a continuation from the still before, one step at most), and each still is made again in the
/// batch, from your prompt, or replaced by your own file.
/// </summary>
public sealed class StillsRunTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly ScriptRunTests.FakeScriptAgent _script = new();
    private readonly StoryboardRunTests.FakeStoryboardAgent _board = new();
    private readonly FakePictures _pictures = new();
    private readonly FakeCheck _check = new();
    private readonly FakeImageComfy _comfy = new();
    private readonly FakeStillPrompts _prompts = new();
    private readonly string _templates;
    private readonly string _projects;

    // The still profile's template and where it puts what it is given.
    private string _template = "{shot.visual}, painted dark fantasy";
    private IReadOnlyList<WorkflowInput> _stillInputs = [.. FakeImageComfy.Inputs, new("negative", "#6.text")];

    /// <summary>The image workflow with a negative prompt node.</summary>
    private const string Workflow = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "" } },
          "2": { "class_type": "TextEncodeQwenImage21", "inputs": { "prompt": "" } },
          "3": { "class_type": "KSampler", "inputs": { "seed": 1 } },
          "4": { "class_type": "EmptyLatentImage", "inputs": { "width": 512, "height": 512 } },
          "5": { "class_type": "SaveImage", "inputs": { "images": ["3", 0] } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "" } }
        }
        """;

    public StillsRunTests()
    {
        _templates = Path.Combine(_engine.DataDirectory, "templates");
        _projects = Path.Combine(_engine.DataDirectory, "media");
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, "qwen.json"), Workflow);
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach.png")];
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Coin.png", page: "https://bg3.wiki/wiki/Soul_Coin")];
    }

    public void Dispose() => _engine.Dispose();

    private Task<IStoryForgeClient> StartAsync() => _engine.StartClientAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new ScriptRunTests.ThreeFacts()));
        services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(_script));
        services.Replace(ServiceDescriptor.Singleton<IStoryboardAgent>(_board));
        services.Replace(ServiceDescriptor.Singleton<IPictureSource>(_pictures));
        services.Replace(ServiceDescriptor.Singleton<IPictureCheck>(_check));
        services.Replace(ServiceDescriptor.Singleton<IComfyUi>(_comfy));
        services.Replace(ServiceDescriptor.Singleton<IStillPromptAgent>(_prompts));
        StoryboardRunTests.UseVoice<StoryboardRunTests.WordsVoice>(services);
        services.AddSingleton<IStageWorker, StillsStage>();
    });

    /// <summary>
    /// The storyboard of <see cref="StoryboardRunTests.Good"/>: 15 shots per segment, the soul coin in
    /// the first and Karlach in the rest; <paramref name="continues"/> says which shots (by place in
    /// their segment) continue the one before; by default every second one.
    /// </summary>
    private static StoryboardOutput Board(Func<int, bool>? continues = null) => new(
        [new("Karlach", "character", ""), new("Soul coin", "object", ""), new("Mammon's mint", "place", "Where every coin is made.")],
        [.. new[] { "S01", "S02", "S03", "S04" }.Select(id => new SegmentShotsOutput(id,
            [.. Enumerable.Range(0, 15).Select(i => new ShotOutput(i * 10, $"{id} shot {i + 1}", i == 0 ? ["Soul coin"] : ["Karlach"], (continues ?? (n => n % 2 == 1))(i)))]))]);

    /// <summary>A project whose references are made and approved, so the stills start.</summary>
    /// <param name="beforeApproval">Runs just before the references are approved, which starts the stills.</param>
    private async Task<Project> ReferencesApprovedAsync(
        IStoryForgeClient client, Func<ProjectSetup, ProjectSetup>? change = null, StoryboardOutput? board = null, Func<Project, Task>? beforeApproval = null)
    {
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with
        {
            ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates },
            Paths = new PathSettings(_projects),
        });
        var references = await client.CreateProfileAsync(ProfileKind.Image, "Qwen edit");
        var starter = await client.GetProfileVersionAsync(references.Id);
        var referencesSaved = await client.SaveProfileVersionAsync(references.Id, starter.Content with { WorkflowTemplate = "qwen.json", Inputs = FakeImageComfy.Inputs });
        var stills = await client.CreateProfileAsync(ProfileKind.Image, "Painted dark fantasy");
        var stillsSaved = await client.SaveProfileVersionAsync(stills.Id, starter.Content with
        {
            WorkflowTemplate = "qwen.json",
            Inputs = _stillInputs,
            PromptTemplate = _template,
            NegativePrompt = "text, watermark",
            Instructions = "Subject first, then setting and light.",
        });
        _board.Answer(board ?? Board());
        _script.Answer(ScriptRunTests.Good());
        var project = await ScriptRunTests.ApprovedFactsAsync(client, s =>
        {
            s = s with
            {
                Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Script or PipelineStage.Voice))],
                References = s.References with { Profile = new ProfileRef(references.Id, referencesSaved.Version), ReviewList = false },
                Stills = s.Stills with { Profile = new ProfileRef(stills.Id, stillsSaved.Version), Size = new GenerationSize("16:9", 1344, 768) },
            };
            return change is null ? s : change(s);
        });
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.NeedsReview);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);
        var made = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(project.Id), v => v.State is StageState.NeedsReview or StageState.Failed);
        Assert.Null(made.Error);
        if (beforeApproval is not null)
        {
            await beforeApproval(project);
        }
        if (made.References.Count == 0)
        {
            await client.ApproveAsync(project.Id, PipelineStage.References, made.Version);
        }
        else
        {
            await client.ApproveSegmentsAsync(project.Id, PipelineStage.References);
        }
        return project;
    }

    private static Task<StillsView> StillsAsync(IStoryForgeClient client, Guid projectId, Func<StillsView, bool>? done = null) =>
        ScriptRunTests.WaitForAsync(() => client.GetStillsAsync(projectId), v => v.State == StageState.Failed || (done?.Invoke(v) ?? v.State is StageState.NeedsReview or StageState.Approved));

    /// <summary>Each shot's key by its label, "S01·2".</summary>
    private static async Task<Dictionary<string, string>> KeysAsync(IStoryForgeClient client, Guid projectId) =>
        (await client.GetStoryboardAsync(projectId)).Shots.ToDictionary(s => s.Label, s => s.Key);

    /// <summary>The still runs (not the reference images) in the order they ran, with the prompt each got.</summary>
    private List<JsonObject> StillRuns()
    {
        lock (_comfy.Workflows)
        {
            return [.. _comfy.Workflows.Where(w => ((string)w["2"]!["inputs"]!["prompt"]!).Contains("still of", StringComparison.Ordinal))];
        }
    }

    private static string Prompt(JsonObject workflow) => (string)workflow["2"]!["inputs"]!["prompt"]!;

    private static string Image(JsonObject workflow) => (string)workflow["1"]!["inputs"]!["image"]!;

    [Fact]
    public async Task Approved_references_make_one_still_per_shot_from_who_is_in_it_to_review()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(StageState.NeedsReview, view.State);
        Assert.Equal(60, view.Stills.Count);
        Assert.All(view.Stills, s => Assert.Equal((StageState.NeedsReview, 1), (s.State, s.Version)));
        Assert.All(view.Stills, s => Assert.True(File.Exists(s.Path)));
        Assert.All(view.Stills, s => Assert.NotEmpty(s.InputHash));
        var keys = await KeysAsync(client, project.Id);
        var first = view.Stills.Single(s => s.Key == keys["S01·1"]).Still!;
        // The soul coin is in the first shot: its reference image goes in, the prompt in the profile's template.
        Assert.Equal(["R02/1"], first.References);
        Assert.Equal("the still of S01-1, painted dark fantasy", first.Prompt);
        Assert.Equal(new GenerationSize("16:9", 1344, 768), first.Size);
        Assert.Equal("qwen.json", first.Workflow);
        Assert.Equal(["R01/1"], view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.References);
        Assert.StartsWith(Path.Combine(_projects, $"Soul Coins – BG3 lore ({project.Id.ToString("N")[..8]})", "stills"), view.Stills[0].Path);

        var runs = StillRuns();
        Assert.Equal(60, runs.Count);
        var coin = runs.Single(r => Prompt(r).Contains("S01-1,", StringComparison.Ordinal));
        Assert.StartsWith("uploaded-R02-1-", Image(coin));
        Assert.Equal("text, watermark", (string)coin["6"]!["inputs"]!["text"]!);
        Assert.Equal((1344, 768), ((int)coin["4"]!["inputs"]!["width"]!, (int)coin["4"]!["inputs"]!["height"]!));
        // Claude wrote every prompt in one go, from the shots and the profile's instructions.
        var request = Assert.Single(_prompts.Requests);
        Assert.Equal(60, request.Shots.Count);
        Assert.Equal("Subject first, then setting and light.", request.Instructions);
        Assert.Equal("S01 shot 1", request.Shots[0].Description);
        Assert.Equal("Soul coin", Assert.Single(request.Shots[0].Pictures).Name);
        Assert.Equal(string.Join(' ', Enumerable.Repeat("word", 10)), request.Shots[0].Heard);
    }

    [Fact]
    public async Task A_continuation_starts_from_the_still_before_one_step_at_most()
    {
        // S01·2, ·3 and ·4 each continue the shot before; ·5 does not.
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: Board(i => i is 1 or 2 or 3));

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        var keys = await KeysAsync(client, project.Id);
        Still Of(string label) => view.Stills.Single(s => s.Key == keys[label]).Still!;
        Assert.Null(Of("S01·1").ContinuesFrom);
        Assert.Equal(keys["S01·1"], Of("S01·2").ContinuesFrom);
        // ·3 would continue a continuation: it starts from the references again, and ·4 from ·3.
        Assert.Null(Of("S01·3").ContinuesFrom);
        Assert.Equal(keys["S01·3"], Of("S01·4").ContinuesFrom);
        Assert.Null(Of("S01·5").ContinuesFrom);
        var second = StillRuns().Single(r => Prompt(r).Contains("S01-2,", StringComparison.Ordinal));
        Assert.Equal("uploaded-" + Path.GetFileName(Of("S01·1").File), Image(second));
        var asked = _prompts.Requests.Single().Shots;
        Assert.True(asked[1].FromShotBefore);
        Assert.Equal(["the shot before", "Karlach"], asked[1].Pictures.Select(p => p.Name));
        Assert.False(asked[2].FromShotBefore);
    }

    [Fact]
    public async Task With_stills_from_text_only_no_reference_goes_in_and_an_edit_workflow_paints_on_a_blank_canvas()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, s => s with { References = s.References with { Consistency = Consistency.TextToImageOnly } });

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.All(view.Stills, s => Assert.Empty(s.Still!.References));
        Assert.All(_prompts.Requests.Single().Shots.Where(s => !s.FromShotBefore), s => Assert.Empty(s.Pictures));
        var keys = await KeysAsync(client, project.Id);
        var first = StillRuns().Single(r => Prompt(r).Contains("S01-1,", StringComparison.Ordinal));
        Assert.Equal("uploaded-blank-1344x768.png", Image(first));
        Assert.Contains(_comfy.Uploaded, f => f == "blank-1344x768.png");
        Assert.Null(view.Stills.Single(s => s.Key == keys["S01·1"]).Still!.ContinuesFrom);
    }

    [Fact]
    public async Task A_still_profile_that_takes_no_picture_cannot_make_stills_from_references()
    {
        var client = await StartAsync();
        // A project made before still profiles were checked at the start: its profile version takes no picture.
        var project = await ReferencesApprovedAsync(client, beforeApproval: async p =>
        {
            var stills = p.Setup.Stills.Profile;
            var current = await client.GetProfileVersionAsync(stills.ProfileId, stills.Version);
            var plain = await client.SaveProfileVersionAsync(stills.ProfileId, current.Content with { Inputs = [.. _stillInputs.Where(i => i.Key != "image")] });
            await using var db = await _engine.DbAsync();
            var entry = await db.Projects.SingleAsync(e => e.Id == p.Id);
            var json = JsonNode.Parse(entry.SetupJson)!.AsObject();
            json["Stills"]!["Profile"]!["Version"] = plain.Version;
            entry.SetupJson = json.ToJsonString();
            await db.SaveChangesAsync();
        });

        var view = await StillsAsync(client, project.Id);

        Assert.Equal(StageState.Failed, view.State);
        Assert.Equal("The still profile does not say where the reference pictures go. Map 'image' (or 'images') to the node that loads them.", view.Error);
        Assert.Empty(_prompts.Requests);
    }

    [Fact]
    public async Task A_prompt_missing_from_the_answer_is_sent_back()
    {
        _prompts.LeaveOut.Add("S02-3");
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(2, _prompts.Requests.Count);
        Assert.Equal(["S02-3 has no prompt."], _prompts.Problems[1]);
    }

    [Fact]
    public async Task Regenerate_waits_in_the_batch_and_Continue_pipeline_makes_the_batch_in_shot_order()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var runs = StillRuns().Count;

        await client.QueueStillAsync(project.Id, keys["S02·5"]);
        await client.QueueStillAsync(project.Id, keys["S01·3"]);
        var queued = await client.GetStillsAsync(project.Id);

        Assert.Equal(runs, StillRuns().Count);   // nothing is made yet
        Assert.Equal([keys["S01·3"], keys["S02·5"]], queued.Stills.Where(s => s.Queued).Select(s => s.Key).Order());

        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Count(s => s.Version == 2) == 2 && v.Stills.All(s => s.State != StageState.Running));

        Assert.All(view.Stills, s => Assert.False(s.Queued));
        Assert.Equal((2, StageState.NeedsReview), (view.Stills.Single(s => s.Key == keys["S01·3"]).Version, view.Stills.Single(s => s.Key == keys["S01·3"]).State));
        var again = StillRuns().Skip(runs).Select(Prompt).ToList();
        Assert.Equal(2, again.Count);
        Assert.StartsWith("the still of S01-3", again[0]);   // in the order of the shots, not of the clicks
        Assert.StartsWith("the still of S02-5", again[1]);
        Assert.Equal(["S01·3"], _prompts.Requests[1].Shots.Select(s => s.Label));
    }

    [Fact]
    public async Task Edit_prompt_makes_the_still_again_with_your_prompt_as_it_is()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueStillAsync(project.Id, keys["S01·3"], "  Karlach laughing by a campfire, still of mine  ");
        Assert.Equal("Karlach laughing by a campfire, still of mine", (await client.GetStillsAsync(project.Id)).Stills.Single(s => s.Key == keys["S01·3"]).QueuedPrompt);
        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });

        var still = view.Stills.Single(s => s.Key == keys["S01·3"]).Still!;
        Assert.Equal(("Karlach laughing by a campfire, still of mine", true), (still.Prompt, still.YourPrompt));
        Assert.Single(_prompts.Requests);   // Claude was not asked
        Assert.Equal("Karlach laughing by a campfire, still of mine", Prompt(StillRuns()[^1]));
    }

    [Fact]
    public async Task Continue_pipeline_with_nothing_in_the_batch_is_refused()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ContinuePipelineAsync(project.Id));

        Assert.Equal("No still is waiting to be made again: click Regenerate on the ones you want first.", refused.Message);
    }

    [Fact]
    public async Task Taking_a_still_out_of_the_batch_leaves_it_as_it_is()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueStillAsync(project.Id, keys["S01·3"]);
        await client.UnqueueStillAsync(project.Id, keys["S01·3"]);

        Assert.DoesNotContain((await client.GetStillsAsync(project.Id)).Stills, s => s.Queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ContinuePipelineAsync(project.Id));
    }

    [Fact]
    public async Task Your_own_file_becomes_the_next_version_to_review_and_nothing_is_made()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var runs = StillRuns().Count;
        var file = Path.Combine(_engine.DataDirectory, "mine.png");
        await File.WriteAllBytesAsync(file, BlankCanvas.Png(1920, 1080));
        await client.QueueStillAsync(project.Id, keys["S01·3"]);

        await client.ReplaceStillAsync(project.Id, keys["S01·3"], file);
        var view = await client.GetStillsAsync(project.Id);

        var cell = view.Stills.Single(s => s.Key == keys["S01·3"]);
        Assert.Equal((2, StageState.NeedsReview, false), (cell.Version, cell.State, cell.Queued));
        Assert.Equal((VersionOrigin.Edited, (int?)1), (cell.Versions[^1].Origin, cell.Versions[^1].BasedOn));
        Assert.True(cell.Still!.YourFile);
        Assert.Equal(new GenerationSize("16:9", 1920, 1080), cell.Still.Size);
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(cell.Path!));
        Assert.Equal(runs, StillRuns().Count);
    }

    [Fact]
    public async Task A_file_that_is_no_picture_is_refused()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var file = Path.Combine(_engine.DataDirectory, "notes.txt");
        await File.WriteAllTextAsync(file, "not a picture");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.ReplaceStillAsync(project.Id, keys["S01·3"], file));

        Assert.StartsWith("notes.txt is not a PNG, JPEG or WebP picture.", refused.Message);
    }

    [Fact]
    public async Task Approving_every_still_approves_the_stage()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);

        var view = await StillsAsync(client, project.Id, v => v.State == StageState.Approved);
        Assert.All(view.Stills, s => Assert.Equal(StageState.Approved, s.State));
    }

    [Fact]
    public async Task Test_render_makes_one_picture_from_the_test_line_in_the_profile_template()
    {
        var client = await StartAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates } });
        var profile = ProfileContent.Empty with
        {
            Provider = "ComfyUI",
            WorkflowTemplate = "qwen.json",
            PromptTemplate = "{shot.visual}, painted dark fantasy",
            Inputs = [new("prompt", "#2.prompt"), new("width", "#4.width"), new("height", "#4.height")],
            Sizes = [new("9:16", 768, 1344), new("16:9", 1344, 768)],
        };

        var render = await client.TestRenderAsync(profile, " a lone knight on a cliff at dusk ");

        Assert.Equal("a lone knight on a cliff at dusk, painted dark fantasy", render.Prompt);
        Assert.Equal(new GenerationSize("16:9", 1344, 768), render.Size);
        Assert.True(File.Exists(render.Path));
        Assert.StartsWith(Path.Combine(_engine.DataDirectory, "test-renders"), render.Path);
        var run = Assert.Single(_comfy.Workflows);
        Assert.Equal("a lone knight on a cliff at dusk, painted dark fantasy", Prompt(run));
        Assert.Empty(_comfy.Uploaded);   // a text-to-image workflow gets no canvas
    }

    [Fact]
    public async Task Test_render_says_plainly_what_the_profile_lacks()
    {
        var client = await StartAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TestRenderAsync(ProfileContent.Empty with { Provider = "ComfyUI" }, "a knight"));

        Assert.Equal("The image profile has no workflow. Pick one in Profiles → Image.", refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => client.TestRenderAsync(ProfileContent.Empty with { Provider = "ComfyUI" }, "  "));
    }

    [Theory]
    [InlineData("{shot.visual}, painted dark fantasy", "a knight", "", "a knight, painted dark fantasy")]
    [InlineData("painted dark fantasy", "a knight", "", "a knight, painted dark fantasy")]
    [InlineData("", "a knight", "", "a knight")]
    [InlineData("{shot.visual}, {characters.sheet}, rim light", "a knight", "", "a knight, rim light")]
    [InlineData("{shot.visual}, {characters.sheet}, rim light", "a knight", "Red skin, horns.", "a knight, Red skin, horns., rim light")]
    public void The_shot_prompt_goes_into_the_profile_template(string template, string prompt, string look, string expected) =>
        Assert.Equal(expected, StillsStage.Compose(template, prompt, look.Length == 0 ? [] : [look]));

    /// <summary>Claude writing still prompts: "the still of S01-2" for every shot asked, unless told to leave one out once.</summary>
    internal sealed class FakeStillPrompts : IStillPromptAgent
    {
        public List<StillPromptRequest> Requests { get; } = [];

        public List<IReadOnlyList<string>> Problems { get; } = [];

        /// <summary>Shots the first answer has no prompt for.</summary>
        public HashSet<string> LeaveOut { get; } = [];

        public Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(StillPromptRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                Problems.Add(problems);
            }
            var first = session is null;
            var prompts = request.Shots
                .Select(s => StillPromptCheck.Name(s.Label))
                .Where(label => !first || !LeaveOut.Contains(label))
                .Select(label => new StillPromptOutput(label, $"the still of {label}"))
                .ToList();
            return Task.FromResult(new ScriptAnswer<StillPromptsOutput>("s", new StillPromptsOutput(prompts)));
        }
    }
}
