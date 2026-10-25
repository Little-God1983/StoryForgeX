using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.References;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The References stage in the pipeline, with stand-in sources, picture check and ComfyUI: it starts
/// when the storyboard is approved, proposes the list, builds the pool, and makes one image per
/// reference, saying how each came about; its gate is always on.
/// </summary>
public sealed partial class ReferencesRunTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly ScriptRunTests.FakeScriptAgent _script = new();
    private readonly StoryboardRunTests.FakeStoryboardAgent _board = new();
    private readonly FakePictures _pictures = new();
    private readonly FakeCheck _check = new();
    private readonly FakeImageComfy _comfy = new();
    private readonly string _templates;
    private readonly string _projects;

    public ReferencesRunTests()
    {
        _templates = Path.Combine(_engine.DataDirectory, "templates");
        _projects = Path.Combine(_engine.DataDirectory, "media");
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, "qwen.json"), FakeImageComfy.Workflow);
        // The storyboard of StoryboardRunTests.Good(): Karlach in most shots, the soul coin in four, Mammon's mint in none.
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach.png")];
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Coin.png", page: "https://bg3.wiki/wiki/Soul_Coin")];
        _check.ByName["Soul coin"] = new PictureVerdict(true, false, "the back", "A tarnished iron coin.");
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
        StoryboardRunTests.UseVoice<StoryboardRunTests.WordsVoice>(services);
    });

    /// <summary>A project whose storyboard is approved, with an image profile for the workflow above.</summary>
    private async Task<Project> StoryboardApprovedAsync(IStoryForgeClient client, Func<ProjectSetup, ProjectSetup>? change = null)
    {
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with
        {
            ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates },
            Paths = new PathSettings(_projects),
        });
        var image = await client.CreateProfileAsync(ProfileKind.Image, "Qwen edit");
        var starter = await client.GetProfileVersionAsync(image.Id);
        var saved = await client.SaveProfileVersionAsync(image.Id, starter.Content with { WorkflowTemplate = "qwen.json", Inputs = FakeImageComfy.Inputs });
        _board.Answer(StoryboardRunTests.Good());
        _script.Answer(ScriptRunTests.Good());
        var project = await ScriptRunTests.ApprovedFactsAsync(client, s =>
        {
            s = s with
            {
                Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Script or PipelineStage.Voice))],
                Stills = s.Stills with { Profile = new ProfileRef(image.Id, saved.Version) },
            };
            return change is null ? s : change(s);
        });
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.NeedsReview);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);
        return project;
    }

    private static Task<ReferencesView> ReferencesAsync(IStoryForgeClient client, Guid projectId, Func<ReferencesView, bool> done) =>
        ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.State == StageState.Failed || done(v));

    private static bool ImagesMade(ReferencesView view) =>
        view.State is StageState.NeedsReview or StageState.Approved && view.Phase == ReferencePhase.Images && view.References.All(r => r.Images.Count > 0);

    [Fact]
    public async Task An_approved_storyboard_proposes_the_list_and_stops_there_for_review()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client);

        var view = await ReferencesAsync(client, project.Id, v => v.State == StageState.NeedsReview);

        Assert.Null(view.Error);
        Assert.Equal(ReferencePhase.List, view.Phase);
        Assert.Equal(["Karlach", "Soul coin"], view.References.Select(r => r.Reference.Name));
        Assert.Equal(new OtherCast("Mammon's mint", CastKind.Place, 0), Assert.Single(view.Others));
        Assert.Equal(2, view.Pool.Count);
        Assert.All(view.Pool, p => Assert.True(File.Exists(p.Path)));
        Assert.All(view.References, r => Assert.Empty(r.Images));
        Assert.Empty(_comfy.Workflows);
        Assert.Equal("S01·2", view.ShotLabels[view.References[0].Reference.Shots[0]]);
    }

    [Fact]
    public async Task Confirming_the_list_makes_one_image_per_reference_and_says_how_each_came_about()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client);
        await ReferencesAsync(client, project.Id, v => v.State == StageState.NeedsReview);

        await client.MakeReferenceImagesAsync(project.Id);
        var view = await ReferencesAsync(client, project.Id, ImagesMade);

        Assert.Null(view.Error);
        var karlach = view.References[0].Images.Single();
        var coin = view.References[1].Images.Single();
        Assert.Equal(("R01/1", StageState.NeedsReview, ImageMaking.FromPicture), (karlach.Key, karlach.State, karlach.Image.Made));
        Assert.Equal((ImageMaking.PartlyMadeUp, "the back"), (coin.Image.Made, coin.Image.Missing));
        Assert.True(File.Exists(karlach.Path));
        Assert.StartsWith(Path.Combine(_projects, $"Soul Coins – BG3 lore ({project.Id.ToString("N")[..8]})", "references"), karlach.Path);
        // Claude's look goes into the reference and into the prompt.
        Assert.Equal("A tarnished iron coin.", view.References[1].Reference.Look);
        var prompts = _comfy.Workflows.Select(w => (string)w["2"]!["inputs"]!["prompt"]!).ToList();
        Assert.Contains(prompts, p => p.Contains("Soul coin") && p.Contains("the back") && p.Contains("A tarnished iron coin."));
        Assert.All(_comfy.Workflows, w => Assert.Equal((1024, 1024), ((int)w["4"]!["inputs"]!["width"]!, (int)w["4"]!["inputs"]!["height"]!)));
        Assert.All(_comfy.Workflows, w => Assert.StartsWith("uploaded-P0", (string)w["1"]!["inputs"]!["image"]!));
    }

    [Fact]
    public async Task Without_the_review_the_images_are_made_straight_away()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });

        var view = await ReferencesAsync(client, project.Id, ImagesMade);

        Assert.Null(view.Error);
        Assert.Equal(StageState.NeedsReview, view.State);
        Assert.Equal(2, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task With_no_usable_picture_nothing_is_made_and_it_cannot_be_approved()
    {
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Tiny.png", 200, 200)];
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });

        var view = await ReferencesAsync(client, project.Id, ImagesMade);

        var coin = view.References[1].Images.Single();
        Assert.Equal((ImageMaking.NoPicture, (string?)null), (coin.Image.Made, coin.Path));
        Assert.Equal("Only pictures under 512 px were found.", coin.Image.Missing);
        Assert.Single(_comfy.Workflows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentAsync(project.Id, PipelineStage.References, coin.Key, coin.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentsAsync(project.Id, PipelineStage.References));
    }

    [Fact]
    public async Task A_picture_that_does_not_show_the_reference_is_passed_over()
    {
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Wyll.png"), FakePictures.Picture("Karlach.png", main: false)];
        _check.ByName["Karlach"] = new PictureVerdict(true, true, "", "Red skin, horns.");
        // The first picture Claude is shown is Wyll's: it says so, and the next one is used.
        _check.Decide = request => request.Name == "Karlach" && request.PicturePath.Contains("P01") ? new PictureVerdict(false, false, "", "") : null;
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });

        var view = await ReferencesAsync(client, project.Id, ImagesMade);

        var karlach = view.References[0].Images.Single();
        Assert.Equal(ImageMaking.FromPicture, karlach.Image.Made);
        Assert.Equal([view.Pool.Single(p => p.Image.Caption == "Karlach.png").Image.Id], karlach.Image.Sources);   // not Wyll's beside it
        Assert.DoesNotContain("P01.png", _comfy.Uploaded);   // Wyll's picture is not sent to Qwen
    }

    [Fact]
    public async Task One_image_is_made_again_on_its_own()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });
        await ReferencesAsync(client, project.Id, ImagesMade);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.References, "R01/1");
        var view = await ReferencesAsync(client, project.Id, v => v.References[0].Images[0].Version == 2);

        Assert.Equal((2, 1), (view.References[0].Images[0].Version, view.References[1].Images[0].Version));
        Assert.Equal(3, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task Approving_every_image_approves_the_references()
    {
        _check.ByName.Clear();
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });
        await ReferencesAsync(client, project.Id, ImagesMade);

        await client.ApproveSegmentsAsync(project.Id, PipelineStage.References);

        var view = await ReferencesAsync(client, project.Id, v => v.State == StageState.Approved);
        Assert.Equal(StageState.Approved, view.State);
        Assert.Equal(StageState.NotStarted, (await client.GetProjectAsync(project.Id)).Stages.Single(s => s.Stage == PipelineStage.Stills).State);
    }

    [Fact]
    public async Task A_project_with_stills_from_text_only_needs_no_references()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { Stills = s.Stills with { Consistency = Consistency.TextToImageOnly } });

        var view = await ReferencesAsync(client, project.Id, v => v.State == StageState.NeedsReview);

        Assert.True(view.TextOnly);
        Assert.Empty(view.References);
        Assert.Empty(_pictures.Searched);
    }

    [Fact]
    public async Task ComfyUI_that_cannot_make_an_image_fails_the_stage_with_its_reason()
    {
        _comfy.Failure = "ComfyUI is not reachable at 127.0.0.1:8188. Start it, or check Settings → ComfyUI.";
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });

        var view = await ReferencesAsync(client, project.Id, _ => false);

        Assert.Equal(StageState.Failed, view.State);
        Assert.Equal("Karlach: ComfyUI is not reachable at 127.0.0.1:8188. Start it, or check Settings → ComfyUI.", view.Error);
    }
}
