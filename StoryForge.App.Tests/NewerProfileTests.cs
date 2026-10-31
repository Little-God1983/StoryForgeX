using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>
/// #75: a stage that failed offers the newer version of the profile it started with, on its failure
/// line: "Use Slow cinematic push v2" moves the stage to it and runs it again.
/// </summary>
public sealed class NewerProfileTests
{
    private readonly FakeStoryForgeClient _client = new();

    /// <summary>Voice, image and video profiles of two versions each; the project started on v1 of all three.</summary>
    private ProjectSetup Setup()
    {
        var content = FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI" };
        var voice = _client.AddProfile(ProfileKind.Voice, "Amira – voice direction", content, content);
        var image = _client.AddProfile(ProfileKind.Image, "Painted dark fantasy", content, content);
        var video = _client.AddProfile(ProfileKind.Video, "Slow cinematic push", content, content);
        var s = StoryboardMatrixTests.Setup();
        return s with
        {
            Voice = s.Voice with { Profile = new ProfileRef(voice.Id, 1) },
            References = s.References with { Profile = new ProfileRef(image.Id, 1) },
            Stills = s.Stills with { Profile = new ProfileRef(image.Id, 1) },
            Clips = s.Clips with { Profile = new ProfileRef(video.Id, 1) },
        };
    }

    private async Task<(ResultMatrixPageViewModel Page, Project Project)> OpenAsync(
        StageState voice = StageState.Approved, StageState stills = StageState.Approved, StageState clips = StageState.NeedsReview, bool newer = true)
    {
        var project = await SeedAsync(voice, stills, clips, newer);
        var page = new ResultMatrixPageViewModel(_client, _ => { });
        page.Show(project);
        await page.ScriptLoading;
        return (page, project);
    }

    /// <summary>A started project whose stages stand as given.</summary>
    private async Task<Project> SeedAsync(
        StageState voice = StageState.Approved, StageState stills = StageState.Approved, StageState clips = StageState.NeedsReview, bool newer = true)
    {
        var setup = Setup();
        if (!newer)
        {
            // Every profile's newest version is the one the project started with.
            setup = setup with { Clips = setup.Clips with { Profile = setup.Clips.Profile with { Version = 2 } } };
        }
        var project = await _client.CreateProjectAsync(setup);
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [],
            [StoryboardMatrixTests.Segment("S01", "Hook")], "", 10, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, voice, voice == StageState.Failed ? "The voice profile has no workflow." : null, [], [], 0, 60);
        _client.Storyboards[project.Id] = StoryboardMatrixTests.Planned(project.Id, StageState.Approved,
            StoryboardMatrixTests.Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved));
        _client.Stills[project.Id] = new StillsView(project.Id, stills, stills == StageState.Failed ? "The image profile has no workflow." : null, [], [], true);
        _client.Clips[project.Id] = new ClipsView(project.Id, clips, clips == StageState.Failed ? "The video profile has no workflow. Pick one in Profiles → Video." : null,
            [], [], stills == StageState.Approved, "Minimax H3", []);
        return project;
    }

    /// <summary>Review of #75, round 2: the main way through: clips fail, the video profile is fixed
    /// in Profiles, and back on the matrix the fixed version is offered (it was not reloaded).</summary>
    [Fact]
    public async Task A_profile_fixed_in_Profiles_is_offered_on_coming_back_to_the_matrix()
    {
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        var project = await SeedAsync(clips: StageState.Failed, newer: false);
        await main.OpenRecentProjectCommand.ExecuteAsync(new ProjectSummary(project.Id, project.Setup.Name, "Clips: failed"));
        var matrix = (ResultMatrixPageViewModel)main.SelectedNavItem!.Page;
        await matrix.ScriptLoading;
        Assert.Null(matrix.Script!.ClipsNewerProfile);

        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Profiles");
        await main.PageShowing;
        await _client.SaveProfileVersionAsync(project.Setup.Clips.Profile.ProfileId, FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI", PromptTemplate = "fixed" });
        main.SelectedNavItem = main.NavItems.Single(item => item.Page == matrix);
        await main.PageShowing;

        Assert.Equal("Use Slow cinematic push v3", matrix.Script!.ClipsNewerProfile);

        // Round 3: by way of Settings after saving, too.
        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Profiles");
        await main.PageShowing;
        await _client.SaveProfileVersionAsync(project.Setup.Clips.Profile.ProfileId, FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI", PromptTemplate = "fixed again" });
        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Settings");
        main.SelectedNavItem = main.NavItems.Single(item => item.Page == matrix);
        await main.PageShowing;
        Assert.Equal("Use Slow cinematic push v4", matrix.Script!.ClipsNewerProfile);
    }

    /// <summary>Review of #75, round 3: a refused move (already moved elsewhere) reloads, so the
    /// offer it refused does not stay on its button.</summary>
    [Fact]
    public async Task A_refused_move_reloads_so_a_gone_offer_goes()
    {
        var (page, project) = await OpenAsync(clips: StageState.Failed);
        await _client.UseNewerProfileAsync(project.Id, PipelineStage.Clips);   // moved from another window
        _client.UseNewerProfileFailure = new InvalidOperationException("The Clips profile has no version newer than 2.");

        await page.Script!.UseNewerClipsProfileCommand.ExecuteAsync(null);

        Assert.Equal("Could not use Slow cinematic push v2: The Clips profile has no version newer than 2.", page.Script.ActionError);
        Assert.Null(page.Script.ClipsNewerProfile);
    }

    [Fact]
    public async Task Failed_clips_offer_the_newer_video_profile_and_make_the_clips_with_it()
    {
        var (page, project) = await OpenAsync(clips: StageState.Failed);
        var script = page.Script!;

        Assert.Equal("Use Slow cinematic push v2", script.ClipsNewerProfile);
        await script.UseNewerClipsProfileCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, PipelineStage.Clips)], _client.NewerProfileUses);
        Assert.Equal(("make", "", (string?)null), Assert.Single(_client.ClipActions));
        Assert.Equal(2, (await _client.GetProjectAsync(project.Id)).Setup.Clips.Profile.Version);
    }

    [Fact]
    public async Task Without_a_newer_version_or_a_failure_nothing_is_offered()
    {
        var (current, _) = await OpenAsync(clips: StageState.Failed, newer: false);
        Assert.Null(current.Script!.ClipsNewerProfile);
        Assert.False(current.Script.UseNewerClipsProfileCommand.CanExecute(null));

        // Clips that did not fail keep what they started with, newer version or not.
        var (fine, _) = await OpenAsync(clips: StageState.NeedsReview);
        Assert.Null(fine.Script!.ClipsNewerProfile);
        Assert.False(fine.Script.UseNewerClipsProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_voice_offers_the_newer_voice_profile_and_speaks_again()
    {
        var (page, project) = await OpenAsync(voice: StageState.Failed, stills: StageState.NotStarted, clips: StageState.NotStarted);
        var script = page.Script!;

        Assert.Equal("Use Amira – voice direction v2", script.VoiceNewerProfile);
        await script.UseNewerVoiceProfileCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, PipelineStage.Voice)], _client.NewerProfileUses);
        Assert.Equal([(project.Id, PipelineStage.Voice)], _client.Regenerated);
    }

    [Fact]
    public async Task Failed_stills_offer_the_newer_image_profile_and_continue()
    {
        var (page, project) = await OpenAsync(stills: StageState.Failed, clips: StageState.NotStarted);
        var script = page.Script!;

        Assert.Equal("Use Painted dark fantasy v2", script.StillsNewerProfile);
        await script.UseNewerStillsProfileCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, PipelineStage.Stills)], _client.NewerProfileUses);
        Assert.Equal(("continue", "", (string?)null), Assert.Single(_client.StillActions));
    }

    [Fact]
    public async Task When_the_newer_version_cannot_run_either_the_reason_shows_and_nothing_runs()
    {
        var (page, _) = await OpenAsync(clips: StageState.Failed);
        _client.UseNewerProfileFailure = new ArgumentException("\"Slow cinematic push\" cannot make clips: it has no workflow.");

        await page.Script!.UseNewerClipsProfileCommand.ExecuteAsync(null);

        Assert.Equal("Could not use Slow cinematic push v2: \"Slow cinematic push\" cannot make clips: it has no workflow.", page.Script.ActionError);
        Assert.Empty(_client.ClipActions);
    }

    /// <summary>Review of #75: when the move went through but the stage would not start again, the
    /// screen kept offering the move it had just made, under "Could not use …".</summary>
    [Fact]
    public async Task Moved_but_not_started_again_shows_the_new_version_and_says_what_did_not_start()
    {
        var (page, project) = await OpenAsync(clips: StageState.Failed);
        _client.StageFailure = new InvalidOperationException("A clip is still being made.");

        await page.Script!.UseNewerClipsProfileCommand.ExecuteAsync(null);

        Assert.Equal(2, (await _client.GetProjectAsync(project.Id)).Setup.Clips.Profile.Version);
        Assert.Null(page.Script.ClipsNewerProfile);
        Assert.Equal("Now on Slow cinematic push v2, but it did not start again: A clip is still being made.", page.Script.ActionError);
    }

    [Fact]
    public async Task Failed_references_offer_the_newer_image_profile_and_try_again()
    {
        var setup = Setup();
        var project = await _client.CreateProjectAsync(setup);
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [StoryboardMatrixTests.Segment("S01", "Hook")], "", 10, 60, []);
        _client.References[project.Id] = new ReferencesView(project.Id, StageState.Failed, "The image profile has no workflow.", [], ReferencePhase.Images, [], [], [], false, new Dictionary<string, string>(), 0);
        var page = new ResultMatrixPageViewModel(_client, _ => { });
        page.Show(project);
        await page.ScriptLoading;
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        var screen = page.References!;

        Assert.Equal("Use Painted dark fantasy v2", screen.NewerProfile);
        await screen.UseNewerProfileCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, PipelineStage.References)], _client.NewerProfileUses);
        Assert.Equal(("make images", ""), Assert.Single(_client.ReferenceActions));
    }

    [Fact]
    public async Task References_moved_but_not_started_again_say_so()
    {
        var setup = Setup();
        var project = await _client.CreateProjectAsync(setup);
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [StoryboardMatrixTests.Segment("S01", "Hook")], "", 10, 60, []);
        _client.References[project.Id] = new ReferencesView(project.Id, StageState.Failed, "The image profile has no workflow.", [], ReferencePhase.Images, [], [], [], false, new Dictionary<string, string>(), 0);
        var page = new ResultMatrixPageViewModel(_client, _ => { });
        page.Show(project);
        await page.ScriptLoading;
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        var screen = page.References!;
        _client.StageFailure = new InvalidOperationException("The references are busy.");

        await screen.UseNewerProfileCommand.ExecuteAsync(null);

        Assert.Equal(2, (await _client.GetProjectAsync(project.Id)).Setup.References.Profile.Version);
        Assert.Null(screen.NewerProfile);
        Assert.Equal("Now on Painted dark fantasy v2, but it did not start again: The references are busy.", screen.ActionError);
    }
}
