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
        // S01·2, ·3 and ·4 each continue the shot before; ·5 does not. The workflow takes a list of pictures.
        _stillInputs = [.. FakeImageComfy.Inputs, new("images", "#1.files_json"), new("negative", "#6.text")];
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
        // One prompt call for the whole batch.
        Assert.Equal(["S01·3", "S02·5"], Assert.Single(_prompts.Requests.Skip(1)).Shots.Select(s => s.Label));
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
    public async Task A_still_that_fails_in_the_batch_stays_in_it_with_your_prompt()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "campfire";

        await client.QueueStillAsync(project.Id, keys["S01·3"], "Karlach by a campfire");
        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]).State == StageState.Failed);

        var still = view.Stills.Single(s => s.Key == keys["S01·3"]);
        Assert.Equal((true, "Karlach by a campfire"), (still.Queued, still.QueuedPrompt));
    }

    [Fact]
    public async Task Regenerate_on_a_still_queued_with_your_prompt_keeps_your_prompt()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueStillAsync(project.Id, keys["S01·3"], "Karlach by a campfire");
        await client.QueueStillAsync(project.Id, keys["S01·3"]);

        Assert.Equal("Karlach by a campfire", (await client.GetStillsAsync(project.Id)).Stills.Single(s => s.Key == keys["S01·3"]).QueuedPrompt);
    }

    [Fact]
    public async Task A_merged_away_shot_takes_its_still_with_it_and_the_stage_can_be_approved()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.MergeShotAsync(project.Id, keys["S01·3"]);
        var view = await client.GetStillsAsync(project.Id);

        Assert.Equal(59, view.Stills.Count);
        Assert.DoesNotContain(view.Stills, s => s.Key == keys["S01·4"]);
        await using (var db = await _engine.DbAsync())
        {
            // Its versions go with it: a key that comes back starts with no pictures of another shot.
            Assert.False(await db.CellVersions.AnyAsync(v => v.Stage == PipelineStage.Stills && v.Key == keys["S01·4"]));
        }
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        Assert.Equal(StageState.Approved, (await StillsAsync(client, project.Id, v => v.State == StageState.Approved)).State);
    }

    [Fact]
    public async Task A_shot_split_off_after_the_stills_gets_its_still_from_Continue_pipeline()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var runs = StillRuns().Count;

        await client.SplitShotAsync(project.Id, keys["S01·3"], 25);
        var split = (await KeysAsync(client, project.Id))["S01·4"];
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);

        // Every still there is approved, but the new shot has none yet: the stage waits for it.
        Assert.Equal(StageState.NeedsReview, (await client.GetStillsAsync(project.Id)).State);
        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Any(s => s.Key == split && s.State == StageState.NeedsReview));
        Assert.Equal(runs + 1, StillRuns().Count);
        Assert.Equal(61, view.Stills.Count);
    }

    [Fact]
    public async Task After_a_failed_run_Continue_pipeline_makes_only_the_stills_still_missing()
    {
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "S02-1,";
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        var failed = await StillsAsync(client, project.Id);
        Assert.Equal(StageState.Failed, failed.State);
        var made = failed.Stills.Count(s => s.Still is not null);
        Assert.Equal(15, made);   // all of S01, then S02·1 failed
        _comfy.Failure = null;
        var runs = StillRuns().Count;

        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Count(s => s.Still is not null) == 60 && v.Stills.All(s => s.State != StageState.Running));

        Assert.Equal(runs + 45, StillRuns().Count);
        Assert.All(view.Stills, s => Assert.Equal(1, s.Version));
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        Assert.Equal(StageState.Approved, (await StillsAsync(client, project.Id, v => v.State == StageState.Approved)).State);
    }

    [Fact]
    public async Task Only_the_last_test_renders_are_kept()
    {
        var client = await StartAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates } });
        var profile = ProfileContent.Empty with { Provider = "ComfyUI", WorkflowTemplate = "qwen.json", Inputs = [new("prompt", "#2.prompt")] };

        var renders = new List<TestRender>();
        for (var i = 0; i <= TestRenders.Kept; i++)
        {
            renders.Add(await client.TestRenderAsync(profile, $"a knight {i}"));
        }

        Assert.False(File.Exists(renders[0].Path));
        Assert.All(renders.Skip(1), r => Assert.True(File.Exists(r.Path)));
    }

    [Fact]
    public async Task Splitting_a_shot_after_the_stills_are_approved_takes_the_approval_back()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        await StillsAsync(client, project.Id, v => v.State == StageState.Approved);
        var keys = await KeysAsync(client, project.Id);

        await client.SplitShotAsync(project.Id, keys["S01·3"], 25);

        Assert.Equal(StageState.NeedsReview, (await client.GetStillsAsync(project.Id)).State);
    }

    [Fact]
    public async Task The_storyboard_cannot_be_cut_while_the_stills_are_made()
    {
        _comfy.Hold = new TaskCompletionSource();
        _comfy.HoldFor = "still of";
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id, v => v.State == StageState.Running);
        var keys = await KeysAsync(client, project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.MergeShotAsync(project.Id, keys["S01·3"]));

        Assert.Equal("The stills are being made from the storyboard. Try again when they are done.", refused.Message);
        _comfy.Hold.SetResult();
        await StillsAsync(client, project.Id);
    }

    [Fact]
    public async Task A_run_with_nothing_waiting_leaves_the_approved_stills_approved()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        await StillsAsync(client, project.Id, v => v.State == StageState.Approved);

        await client.RegenerateAsync(project.Id, PipelineStage.Stills);
        await StillsAsync(client, project.Id, v => v.State != StageState.Running);

        Assert.Equal(StageState.Approved, (await client.GetStillsAsync(project.Id)).State);
    }

    [Fact]
    public async Task A_picture_of_another_shape_cannot_replace_a_still()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var file = Path.Combine(_engine.DataDirectory, "portrait.png");
        await File.WriteAllBytesAsync(file, BlankCanvas.Png(1080, 1920));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.ReplaceStillAsync(project.Id, keys["S01·3"], file));

        Assert.StartsWith("portrait.png is 1080×1920; the stills of this project are 16:9.", refused.Message);
    }

    [Fact]
    public async Task A_test_render_leaves_the_last_ones_of_other_profiles_alone()
    {
        var client = await StartAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates } });
        var profile = ProfileContent.Empty with { Provider = "ComfyUI", WorkflowTemplate = "qwen.json", Inputs = [new("prompt", "#2.prompt")] };

        var mine = await client.TestRenderAsync(profile, "a knight");
        var other = await client.TestRenderAsync(profile with { PromptTemplate = "{shot.visual}, oil paint" }, "a dragon");

        Assert.True(File.Exists(mine.Path));   // shown in another profile's editor
        Assert.True(File.Exists(other.Path));
    }

    [Fact]
    public async Task Making_a_still_again_in_the_batch_keeps_the_approval_of_the_one_before()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·3"], 1);

        await client.QueueStillAsync(project.Id, keys["S01·3"]);
        await client.ContinuePipelineAsync(project.Id);
        await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });
        await client.SelectSegmentVersionAsync(project.Id, PipelineStage.Stills, keys["S01·3"], 1);

        var still = (await client.GetStillsAsync(project.Id)).Stills.Single(s => s.Key == keys["S01·3"]);
        Assert.Equal((1, StageState.Approved, (int?)1), (still.Version, still.State, still.ApprovedVersion));
    }

    [Fact]
    public async Task Making_one_still_again_on_its_own_uses_the_prompt_you_queued()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueStillAsync(project.Id, keys["S01·3"], "Karlach by a campfire, still of mine");
        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });

        Assert.Equal(("Karlach by a campfire, still of mine", true), (view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.Prompt, view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.YourPrompt));
    }

    [Fact]
    public async Task A_run_cut_off_after_its_last_still_is_brought_back_by_Continue_pipeline()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        // As when the app closed after the last still was stored, before the run was done.
        await using (var db = await _engine.DbAsync())
        {
            var whole = await db.Cells.SingleAsync(c => c.ProjectId == project.Id && c.Stage == PipelineStage.Stills && c.Key == "");
            whole.State = StageState.Failed;
            whole.Error = "StoryForge was closed while this stage ran. Retry runs it again.";
            whole.CurrentVersion = null;
            await db.SaveChangesAsync();
        }
        var runs = StillRuns().Count;

        await client.ContinuePipelineAsync(project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);

        Assert.Equal(StageState.Approved, (await StillsAsync(client, project.Id, v => v.State == StageState.Approved)).State);
        Assert.Equal(runs, StillRuns().Count);   // nothing was missing: nothing was made
    }

    [Fact]
    public async Task A_workflow_that_takes_one_picture_gets_one_and_the_prompt_names_only_that()
    {
        _stillInputs = [.. FakeImageComfy.Inputs, new("negative", "#6.text")];   // 'image', no 'images'
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: Board(i => i == 1));

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var asked = _prompts.Requests.Single().Shots;
        Assert.Equal(["the shot before"], asked[1].Pictures.Select(p => p.Name));   // S01·2 continues S01·1
        Assert.Empty(view.Stills.Single(s => s.Key == keys["S01·2"]).Still!.References);
        Assert.Equal(["Karlach"], asked[2].Pictures.Select(p => p.Name));
    }

    [Fact]
    public async Task Who_gets_no_picture_still_goes_into_the_prompt_by_their_look()
    {
        // One picture only, and S01·2 continues S01·1: the still before is the picture, Karlach is in words.
        _template = "{shot.visual}, {characters.sheet}";
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: Board(i => i == 1));

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var asked = _prompts.Requests.Single().Shots[1];
        Assert.Equal(["Karlach"], asked.Described.Select(p => p.Name));
        Assert.Equal("the still of S01-2, Karlach as the picture shows.", view.Stills.Single(s => s.Key == keys["S01·2"]).Still!.Prompt);
    }

    [Fact]
    public async Task A_reference_whose_picture_is_gone_still_goes_in_by_its_look()
    {
        _template = "{shot.visual}, {characters.sheet}";
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, beforeApproval: async p =>
        {
            // Karlach's image file deleted from the project folder by hand.
            var karlach = (await client.GetReferencesAsync(p.Id)).References.Single(r => r.Reference.Name == "Karlach").Images.Single();
            File.Delete(karlach.Path!);
        });

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var third = _prompts.Requests.Single().Shots[2];
        Assert.Empty(third.Pictures);
        Assert.Equal(["Karlach"], third.Described.Select(p => p.Name));
        Assert.Equal("the still of S01-3, Karlach as the picture shows.", view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.Prompt);
    }

    [Fact]
    public async Task Each_reference_picture_is_uploaded_once_a_run()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        var uploadedBefore = _comfy.Uploaded.Count;

        await StillsAsync(client, project.Id);

        // 60 shots, two references and the stills before for 28 continuations.
        var stills = _comfy.Uploaded.Skip(uploadedBefore).ToList();
        Assert.Equal(stills.Distinct().Count(), stills.Count);
    }

    [Fact]
    public async Task The_stills_cannot_be_approved_as_a_whole_while_some_wait_for_review()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveAsync(project.Id, PipelineStage.Stills, 1));
        foreach (var still in (await client.GetStillsAsync(project.Id)).Stills)
        {
            await client.ApproveSegmentAsync(project.Id, PipelineStage.Stills, still.Key, still.Version);
        }
        await client.ApproveAsync(project.Id, PipelineStage.Stills, 1);
        Assert.Equal(StageState.Approved, (await client.GetStillsAsync(project.Id)).State);
    }

    [Fact]
    public void A_still_is_out_of_date_when_the_words_heard_or_a_look_change()
    {
        var profile = new ProfileRef(Guid.NewGuid(), 1);
        var setup = new ProjectSetup("Soul Coins", "brief", [], new ResearchSetup("Claude CLI", "", profile), new ScriptSetup("Claude CLI", "", profile, profile),
            new VoiceSetup("ComfyUI", "Breeze TTS", profile), new ReferencesSetup("ComfyUI", "Qwen Image 2.1 edit", profile, Consistency.ReferenceImages),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", profile, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", profile, 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 60, "English", AssemblyTarget.FfmpegWithFcpxml), [], RunMode.StopAtGates);
        var shot = new Shot("S01/a", "S01", 0, 10, 0, 5, "Karlach in the crypt", ["Karlach"], false);
        IReadOnlyList<StillsStage.ShotPicture> karlach = [new("R01/1", 1, "Karlach", "Red skin, horns.", "k.png")];
        var hash = StillsStage.Hash(setup, shot, karlach, "word word", karlach, null, null);

        Assert.NotEqual(hash, StillsStage.Hash(setup, shot, karlach, "other words", karlach, null, null));
        Assert.NotEqual(hash, StillsStage.Hash(setup, shot, karlach, "word word", [karlach[0] with { Look = "Red skin, one horn." }], null, null));
    }

    [Fact]
    public async Task A_shot_description_can_be_changed_while_the_stills_are_made()
    {
        _comfy.Hold = new TaskCompletionSource();
        _comfy.HoldFor = "still of";
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id, v => v.State == StageState.Running);
        var keys = await KeysAsync(client, project.Id);

        await client.EditShotAsync(project.Id, keys["S02·3"], new ShotChange(Description: "Karlach at the gate"));

        Assert.Equal("Karlach at the gate", (await client.GetStoryboardAsync(project.Id)).Shots.Single(s => s.Key == keys["S02·3"]).Shot.Description);
        _comfy.Hold.SetResult();
        await StillsAsync(client, project.Id);
    }

    [Fact]
    public async Task Nothing_is_stale_in_a_project_just_made()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        Assert.All((await client.GetScriptAsync(project.Id)).Segments, s => Assert.Null(s.Stale));
        Assert.All((await client.GetVoiceAsync(project.Id)).Segments, s => Assert.Null(s.Stale));
        Assert.All((await client.GetStoryboardAsync(project.Id)).Shots, s => Assert.Null(s.Stale));
        Assert.All((await client.GetReferencesAsync(project.Id)).References.SelectMany(r => r.Images), i => Assert.Null(i.Stale));
        Assert.All((await client.GetStillsAsync(project.Id)).Stills, s => Assert.Null(s.Stale));
    }

    [Fact]
    public async Task Editing_a_segment_puts_its_voice_shots_and_stills_out_of_date_and_nothing_else()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var script = await client.GetScriptAsync(project.Id);
        var s02 = script.Segments.Single(s => s.Id == "S02");

        await client.EditSegmentAsync(project.Id, "S02", s02.Title, s02.Narration + " And one more sentence.");
        if ((await client.GetScriptAsync(project.Id)).Segments.Single(s => s.Id == "S02") is { State: StageState.NeedsReview } edited)
        {
            await client.ApproveSegmentAsync(project.Id, PipelineStage.Script, "S02", edited.Version);
        }

        Assert.Null((await client.GetScriptAsync(project.Id)).Segments.Single(s => s.Id == "S02").Stale);   // what you changed is up to date
        var voice = (await client.GetVoiceAsync(project.Id)).Segments;
        Assert.Equal("S02's script changed since it was spoken.", voice.Single(s => s.Id == "S02").Stale);
        Assert.All(voice.Where(s => s.Id != "S02"), s => Assert.Null(s.Stale));
        var shots = (await client.GetStoryboardAsync(project.Id)).Shots;
        Assert.All(shots, s => Assert.Equal(s.SegmentId == "S02" ? "S02's voice is out of date." : null, s.Stale));
        var stills = (await client.GetStillsAsync(project.Id)).Stills;
        var labels = shots.ToDictionary(s => s.Key, s => s.Label);
        Assert.All(stills, s => Assert.Equal(labels[s.Key].StartsWith("S02", StringComparison.Ordinal) ? $"Shot {labels[s.Key]} is out of date." : null, s.Stale));
        Assert.Equal(StageState.NeedsReview, stills[0].State);   // a stale cell keeps its state and its version
    }

    [Fact]
    public async Task A_segment_spoken_again_puts_its_shots_out_of_date()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Voice, "S03");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments.Single(s => s.Id == "S03") is { Version: 2, State: not StageState.Running });

        var shots = (await client.GetStoryboardAsync(project.Id)).Shots;
        Assert.All(shots, s => Assert.Equal(s.SegmentId == "S03" ? "S03's voice changed since the shot was cut." : null, s.Stale));
        Assert.Null((await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S03").Stale);
    }

    [Fact]
    public async Task A_changed_look_puts_the_reference_image_and_only_the_stills_using_it_out_of_date()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var references = await client.GetReferencesAsync(project.Id);
        // Karlach's picture goes into the stills that do not continue the one before (the workflow takes
        // one picture): 7 a segment, and 6 more start from those. In words she is in 14 a segment.
        Assert.Equal(52, references.StillsUsing["R01"]);
        Assert.Equal(56, references.StillsShowing["R01"]);
        // The coin is pictured in each segment's first shot, and the second one starts from it.
        Assert.Equal(8, references.StillsUsing["R02"]);
        Assert.Equal(8, references.StillsShowing["R02"]);

        await client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: "R02", Look: "A coin of black iron with a screaming face."));

        var image = (await client.GetReferencesAsync(project.Id)).References.Single(r => r.Reference.Id == "R02").Images.Single();
        Assert.Equal("Soul coin's look or pictures changed since this image was made.", image.Stale);
        var stills = (await client.GetStillsAsync(project.Id)).Stills;
        Assert.Equal(4, stills.Count(s => s.Stale == "Soul coin's image is out of date."));
        // Each segment's second shot starts from the coin's still: out of date with it.
        Assert.Equal(4, stills.Count(s => s.Stale is { } why && why.StartsWith("The still of S0", StringComparison.Ordinal) && why.EndsWith("·1 it starts from is out of date.", StringComparison.Ordinal)));
        Assert.Equal(52, stills.Count(s => s.Stale is null));
    }

    [Fact]
    public async Task Discarding_a_picture_leaves_the_references_without_it_as_they_are()
    {
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach.png"), FakePictures.Picture("Karlach2.png")];
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var references = await client.GetReferencesAsync(project.Id);
        var second = references.Pool.Single(p => p.Image.Url.EndsWith("Karlach2.png", StringComparison.Ordinal)).Image.Id;

        await client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: second));

        // The coin never had the picture: its image and the stills of its shots stay up to date.
        Assert.Null((await client.GetReferencesAsync(project.Id)).References.Single(r => r.Reference.Name == "Soul coin").Images.Single().Stale);
        var shots = (await client.GetStoryboardAsync(project.Id)).Shots;
        var coinShots = shots.Where(s => s.Number == 1).Select(s => s.Key).ToHashSet();
        Assert.All((await client.GetStillsAsync(project.Id)).Stills.Where(s => coinShots.Contains(s.Key)), s => Assert.Null(s.Stale));
    }

    [Fact]
    public async Task The_warnings_count_per_image_too()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        var references = await client.GetReferencesAsync(project.Id);

        Assert.Equal(references.StillsUsing["R01"], references.StillsUsingImage["R01/1"]);
        Assert.False(references.StillsUsingImage.ContainsKey("R01/2"));
    }

    [Fact]
    public async Task A_still_made_again_is_up_to_date_and_the_one_continuing_it_is_not()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        // S01·2 continues S01·1.
        await client.QueueStillAsync(project.Id, keys["S01·1"]);
        await client.ContinuePipelineAsync(project.Id);
        var view = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·1"]) is { Version: 2, State: not StageState.Running });

        Assert.Null(view.Stills.Single(s => s.Key == keys["S01·1"]).Stale);
        Assert.Equal("The still of S01·1 it starts from was made again since.", view.Stills.Single(s => s.Key == keys["S01·2"]).Stale);
        Assert.Null(view.Stills.Single(s => s.Key == keys["S01·3"]).Stale);
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
