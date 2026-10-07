using System.Text.Json;
using StoryForge.Client;

namespace StoryForge.Engine.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly EngineTestHost _engine = new();

    public void Dispose() => _engine.Dispose();

    /// <summary>A setup that passes every check, with one profile of each kind created for it.</summary>
    private static async Task<ProjectSetup> ValidSetup(IStoryForgeClient client, string name = "Soul Coins – BG3 lore")
    {
        async Task<ProfileRef> Profile(ProfileKind kind)
        {
            var existing = (await client.GetProfilesAsync()).FirstOrDefault(p => p.Kind == kind)
                ?? await client.CreateProfileAsync(kind, $"{kind} default");
            return new ProfileRef(existing.Id, existing.LatestVersion);
        }

        return new ProjectSetup(
            name,
            "Make a lore video about the soul coins from Baldur's Gate 3.",
            ["bg3.wiki", "forgottenrealms.fandom.com"],
            new WritingSetup("Claude CLI", "", await Profile(ProfileKind.Research), await Profile(ProfileKind.Script), await Profile(ProfileKind.Storyboard)),
            new VoiceSetup("ComfyUI", "Breeze TTS", await Profile(ProfileKind.Voice)),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", await Profile(ProfileKind.Image), Consistency.ReferenceImages, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", await Profile(ProfileKind.Video), 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 240, "English", AssemblyTarget.FfmpegWithFcpxml),
            [PipelineStage.Research, PipelineStage.Script, PipelineStage.Storyboard, PipelineStage.References, PipelineStage.Stills, PipelineStage.Assembly],
            RunMode.StopAtGates);
    }

    // Records compare their lists by reference; the same JSON means the same setup.
    private static void AssertSameSetup(ProjectSetup expected, ProjectSetup actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    [Fact]
    public async Task A_fresh_engine_has_no_recent_projects()
    {
        var client = await _engine.StartClientAsync();

        Assert.Empty(await client.GetRecentProjectsAsync());
    }

    [Fact]
    public async Task A_new_project_keeps_every_choice_and_has_not_started()
    {
        var client = await _engine.StartClientAsync();
        var setup = await ValidSetup(client);

        var created = await client.CreateProjectAsync(setup);
        var loaded = await client.GetProjectAsync(created.Id);

        AssertSameSetup(setup, loaded.Setup);
        Assert.Equal(Enum.GetValues<PipelineStage>(), loaded.Stages.Select(s => s.Stage));
        Assert.All(loaded.Stages, s => Assert.Equal(StageState.NotStarted, s.State));
    }

    [Fact]
    public async Task Projects_survive_a_restart_and_are_listed_newest_first()
    {
        var first = await _engine.StartClientAsync();
        var older = await first.CreateProjectAsync(await ValidSetup(first, "Older"));
        var newer = await first.CreateProjectAsync(await ValidSetup(first, "Newer"));

        var second = await _engine.StartClientAsync();

        Assert.Equal(
            [new ProjectSummary(newer.Id, "Newer", "Not started"), new ProjectSummary(older.Id, "Older", "Not started")],
            await second.GetRecentProjectsAsync());
        AssertSameSetup(newer.Setup, (await second.GetProjectAsync(newer.Id)).Setup);
    }

    [Fact]
    public async Task A_project_keeps_the_profile_version_it_started_with()
    {
        var client = await _engine.StartClientAsync();
        var setup = await ValidSetup(client);
        var project = await client.CreateProjectAsync(setup);
        var image = setup.Stills.Profile;
        var v1 = await client.GetProfileVersionAsync(image.ProfileId);

        await client.SaveProfileVersionAsync(image.ProfileId, v1.Content with { PromptTemplate = "changed" });

        Assert.Equal(new ProfileRef(image.ProfileId, 1), (await client.GetProjectAsync(project.Id)).Setup.Stills.Profile);
    }

    [Fact]
    public async Task The_storyboard_and_references_gates_are_always_on()
    {
        var client = await _engine.StartClientAsync();
        var setup = await ValidSetup(client) with { Gates = [PipelineStage.Stills, PipelineStage.Research, PipelineStage.Stills] };

        var project = await client.CreateProjectAsync(setup);

        Assert.Equal(
            [PipelineStage.Research, PipelineStage.Storyboard, PipelineStage.References, PipelineStage.Stills],
            project.Setup.Gates);
    }

    [Fact]
    public async Task Name_brief_and_sources_are_tidied()
    {
        var client = await _engine.StartClientAsync();
        var setup = await ValidSetup(client) with { Name = "  Soul Coins  ", Brief = " A brief. ", ResearchSources = [" bg3.wiki ", "", "  "] };

        var project = await client.CreateProjectAsync(setup);

        Assert.Equal("Soul Coins", project.Setup.Name);
        Assert.Equal("A brief.", project.Setup.Brief);
        Assert.Equal(["bg3.wiki"], project.Setup.ResearchSources);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("brief")]
    [InlineData("aspect")]
    [InlineData("delivery")]
    [InlineData("target")]
    [InlineData("still size")]
    [InlineData("clip size")]
    [InlineData("clip length")]
    public async Task Missing_or_impossible_choices_are_refused_and_nothing_is_saved(string field)
    {
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);
        var bad = field switch
        {
            "name" => s with { Name = "  " },
            "brief" => s with { Brief = "" },
            "aspect" => s with { Output = s.Output with { Aspect = "4:3" } },
            "delivery" => s with { Output = s.Output with { Width = 0 } },
            "target" => s with { Output = s.Output with { TargetSeconds = 0 } },
            "still size" => s with { Stills = s.Stills with { Size = new GenerationSize("16:9", 1344, 0) } },
            "clip size" => s with { Clips = s.Clips with { Size = new GenerationSize("16:9", -1, 720) } },
            _ => s with { Clips = s.Clips with { MaxClipSeconds = 0 } },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(bad));

        Assert.Empty(await client.GetRecentProjectsAsync());
    }

    [Theory]
    [InlineData("stills")]
    [InlineData("clips")]
    public async Task A_generation_size_for_another_aspect_is_refused(string stage)
    {
        // A 16:9 still in a 9:16 video would render every shot the wrong shape.
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);
        var portrait = s with { Output = s.Output with { Aspect = "9:16", Width = 1080, Height = 1920 } };
        var bad = stage == "stills"
            ? portrait with { Clips = s.Clips with { Size = new GenerationSize("9:16", 720, 1280) } }
            : portrait with { Stills = s.Stills with { Size = new GenerationSize("9:16", 768, 1344) } };

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(bad));
    }

    [Theory]
    [InlineData("writing")]
    [InlineData("voice")]
    [InlineData("stills")]
    [InlineData("clips")]
    [InlineData("output")]
    public async Task A_missing_part_is_refused_as_an_argument_not_a_crash(string part)
    {
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);
        var bad = part switch
        {
            "writing" => s with { Writing = null! },
            "voice" => s with { Voice = null! },
            "stills" => s with { Stills = null! },
            "clips" => s with { Clips = null! },
            _ => s with { Output = null! },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(bad));
    }

    [Theory]
    [InlineData("portrait delivery")]
    [InlineData("almost square delivery")]
    [InlineData("landscape still")]
    [InlineData("square delivery")]
    [InlineData("language")]
    [InlineData("gate")]
    [InlineData("mode")]
    [InlineData("consistency")]
    [InlineData("assembly")]
    public async Task Choices_that_do_not_fit_or_do_not_exist_are_refused(string field)
    {
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);
        var bad = field switch
        {
            "portrait delivery" => s with { Output = s.Output with { Width = 1080, Height = 1920 } },   // on a 16:9 project
            "almost square delivery" => s with { Output = s.Output with { Width = 1000, Height = 999 } },
            "landscape still" => s with
            {
                Output = s.Output with { Aspect = "9:16", Width = 1080, Height = 1920 },
                Stills = s.Stills with { Size = new GenerationSize("9:16", 1344, 768) },
                Clips = s.Clips with { Size = new GenerationSize("9:16", 720, 1280) },
            },
            "square delivery" => s with { Output = s.Output with { Width = 1080, Height = 1080 } },
            "language" => s with { Output = s.Output with { Language = "  " } },
            "gate" => s with { Gates = [(PipelineStage)42] },
            "mode" => s with { Mode = (RunMode)7 },
            "consistency" => s with { Stills = s.Stills with { Consistency = (Consistency)9 } },
            _ => s with { Output = s.Output with { Assembly = (AssemblyTarget)5 } },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(bad));
    }

    [Fact]
    public async Task A_missing_model_means_the_providers_default()
    {
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);

        var project = await client.CreateProjectAsync(s with { Voice = s.Voice with { Model = null! } });

        Assert.Equal("", project.Setup.Voice.Model);
    }

    [Fact]
    public async Task A_profile_version_that_does_not_exist_is_refused()
    {
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(
            s with { Voice = s.Voice with { Profile = s.Voice.Profile with { Version = 9 } } }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(
            s with { Voice = s.Voice with { Profile = new ProfileRef(Guid.NewGuid(), 1) } }));
    }

    [Fact]
    public async Task A_profile_of_another_kind_is_refused()
    {
        // An image profile in the script slot would hand the script writer image instructions.
        var client = await _engine.StartClientAsync();
        var s = await ValidSetup(client);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProjectAsync(
            s with { Writing = s.Writing with { Script = s.Stills.Profile } }));

        Assert.Contains("script", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Asking_for_a_project_that_does_not_exist_says_so()
    {
        var client = await _engine.StartClientAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetProjectAsync(Guid.NewGuid()));
    }
}
