using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.Assembly;
using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.Settings;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>
/// #71 on screen: the Assembly shows the timeline file beside the video with Open folder, and Send to
/// Resolve with a project name (the StoryForge X project's to start with). Settings → Resolve holds
/// the paths to Resolve and its Python.
/// </summary>
public sealed class AssemblyResolveTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly List<string> _opened = [];

    private static readonly Guid Id = Guid.Parse("5d1e0000-0000-0000-0000-000000000071");

    private const string Timeline = @"C:\media\assembly\video-1.fcpxml";

    private static AssemblyView Review(string? timeline = Timeline, StageState state = StageState.NeedsReview) =>
        new(Id, state, null, [], 1, null, [new ResultVersion(1, VersionOrigin.Generated, DateTimeOffset.UnixEpoch, null)],
            new AssembledVideo(new CutList([], -20, []), @"media\assembly\video-1.mp4", 60, new GenerationSize("16:9", 1920, 1080),
                new Dictionary<string, int>(), new Dictionary<string, int>(), Timeline: timeline is null ? null : @"media\assembly\video-1.fcpxml"),
            @"C:\media\assembly\video-1.mp4", [], true, null)
        {
            TimelinePath = timeline,
        };

    private async Task<AssemblyViewModel> OpenAsync(AssemblyView view, string projectName = "Dark Eye – DSA lore")
    {
        _client.Assembly[Id] = view;
        var screen = new AssemblyViewModel(_client, Id, projectName, _opened.Add);
        await screen.LoadAsync();
        return screen;
    }

    [Fact]
    public async Task The_timeline_file_shows_beside_the_video_and_its_folder_opens()
    {
        var screen = await OpenAsync(Review());

        Assert.True(screen.HasTimeline);
        Assert.Equal(Timeline, screen.TimelinePath);
        screen.OpenTimelineFolderCommand.Execute(null);
        Assert.Equal([@"C:\media\assembly"], _opened);
    }

    [Fact]
    public async Task Send_to_Resolve_names_the_project_after_this_one_and_says_what_arrived()
    {
        var screen = await OpenAsync(Review());

        Assert.Equal("Dark Eye – DSA lore", screen.ResolveProjectName);
        screen.ResolveProjectName = "  Dark Eye cut 1 ";
        _client.ResolveAnswer = new SentToResolve(true, "done", null, "Dark Eye – DSA lore", 11);
        await screen.SendToResolveCommand.ExecuteAsync(null);

        Assert.Equal([(Id, 1, "Dark Eye cut 1")], _client.ResolveSends);
        Assert.Equal("In Resolve as \"Dark Eye cut 1\": timeline \"Dark Eye – DSA lore\" with 11 clips.", screen.ResolveText);
        Assert.False(screen.ResolveFailed);
    }

    [Fact]
    public async Task A_send_that_failed_says_why_and_one_that_went_in_with_a_problem_says_both()
    {
        var screen = await OpenAsync(Review());

        _client.ResolveAnswer = new SentToResolve(false, "create", "There is already a project called 'Dark Eye – DSA lore' in Resolve - nothing was created.", null, 0);
        await screen.SendToResolveCommand.ExecuteAsync(null);
        Assert.Equal(("There is already a project called 'Dark Eye – DSA lore' in Resolve - nothing was created.", true), (screen.ResolveText, screen.ResolveFailed));

        _client.ResolveAnswer = new SentToResolve(true, "done", "The timeline imported, but Resolve set it to 25 fps.", "Dark Eye", 11);
        await screen.SendToResolveCommand.ExecuteAsync(null);
        Assert.Equal(("In Resolve as \"Dark Eye – DSA lore\": timeline \"Dark Eye\" with 11 clips. The timeline imported, but Resolve set it to 25 fps.", false),
            (screen.ResolveText, screen.ResolveFailed));
    }

    /// <summary>Review of #71: the last send's answer stayed on screen under another version.</summary>
    [Fact]
    public async Task The_answer_of_a_send_belongs_to_its_version_and_goes_when_another_is_shown()
    {
        var two = Review() with
        {
            Version = 2,
            Versions = [new ResultVersion(1, VersionOrigin.Generated, DateTimeOffset.UnixEpoch, null), new ResultVersion(2, VersionOrigin.Generated, DateTimeOffset.UnixEpoch, null)],
        };
        var screen = await OpenAsync(two);
        _client.AssemblyVersions[(Id, 1)] = two with { Version = 1 };
        _client.ResolveAnswer = new SentToResolve(false, "create", "There is already a project called 'Dark Eye – DSA lore' in Resolve - nothing was created.", null, 0);
        await screen.SendToResolveCommand.ExecuteAsync(null);

        await screen.LoadAsync();   // the same version again: the answer stays
        Assert.True(screen.ResolveFailed);

        await screen.Versions[0].ShowCommand.ExecuteAsync(null);
        Assert.Equal((1, "", false), (screen.Version, screen.ResolveText, screen.ResolveFailed));
    }

    /// <summary>Review of #71, round 2: a send that comes back after another version was picked
    /// says which version it sent.</summary>
    [Fact]
    public async Task A_send_that_finishes_under_another_version_names_the_version_it_sent()
    {
        var two = Review() with
        {
            Version = 2,
            Versions = [new ResultVersion(1, VersionOrigin.Generated, DateTimeOffset.UnixEpoch, null), new ResultVersion(2, VersionOrigin.Generated, DateTimeOffset.UnixEpoch, null)],
        };
        var screen = await OpenAsync(two);
        _client.AssemblyVersions[(Id, 1)] = two with { Version = 1 };
        var gate = new TaskCompletionSource();
        _client.ResolveGate = gate.Task;
        _client.ResolveAnswer = new SentToResolve(true, "done", null, "Dark Eye – DSA lore", 11);

        var sending = screen.SendToResolveCommand.ExecuteAsync(null);
        await screen.Versions[0].ShowCommand.ExecuteAsync(null);
        Assert.Equal("Sending… this can take a minute if Resolve is not open yet.", screen.ResolveText);
        gate.SetResult();
        await sending;

        Assert.Equal("Version 2: In Resolve as \"Dark Eye – DSA lore\": timeline \"Dark Eye – DSA lore\" with 11 clips.", screen.ResolveText);
    }

    /// <summary>Review of #71, round 2: "no timeline file" only for a version that has none, and
    /// worded for what can be done (Render again needs an edit).</summary>
    [Fact]
    public async Task Only_a_version_without_a_timeline_says_it_has_none()
    {
        var before = await OpenAsync(Review() with { Version = null, Versions = [], Video = null, Path = null, State = StageState.NotStarted, TimelinePath = null });
        Assert.False(before.MissesTimeline);

        var without = await OpenAsync(Review(timeline: null));
        Assert.True(without.MissesTimeline);

        var with = await OpenAsync(Review());
        Assert.False(with.MissesTimeline);
    }

    [Fact]
    public async Task While_it_sends_it_says_so_and_cannot_be_sent_twice()
    {
        var screen = await OpenAsync(Review());
        var gate = new TaskCompletionSource();
        _client.ResolveGate = gate.Task;

        var sending = screen.SendToResolveCommand.ExecuteAsync(null);

        Assert.True(screen.IsSending);
        Assert.Equal("Sending… this can take a minute if Resolve is not open yet.", screen.ResolveText);
        Assert.False(screen.SendToResolveCommand.CanExecute(null));
        gate.SetResult();
        await sending;
        Assert.True(screen.SendToResolveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_a_timeline_a_name_or_while_the_video_is_cut_nothing_is_sent()
    {
        var none = await OpenAsync(Review(timeline: null));
        Assert.False(none.HasTimeline);
        Assert.False(none.SendToResolveCommand.CanExecute(null));
        Assert.False(none.OpenTimelineFolderCommand.CanExecute(null));

        var unnamed = await OpenAsync(Review(), projectName: "");
        Assert.False(unnamed.SendToResolveCommand.CanExecute(null));
        unnamed.ResolveProjectName = "Dark Eye";
        Assert.True(unnamed.SendToResolveCommand.CanExecute(null));

        var cutting = await OpenAsync(Review(state: StageState.Running));
        Assert.False(cutting.SendToResolveCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_matrix_opens_the_assembly_with_the_projects_name_for_Resolve()
    {
        var project = await _client.CreateProjectAsync(StoryboardMatrixTests.Setup());
        _client.Assembly[project.Id] = Review() with { ProjectId = project.Id };
        var page = new ResultMatrixPageViewModel(_client, _opened.Add);
        page.Show(project);
        await page.ScriptLoading;

        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.Assembly));

        Assert.Equal("Soul Coins – BG3 lore", page.Assembly!.ResolveProjectName);
    }

    [Fact]
    public void The_resolve_card_holds_its_two_paths_and_the_ffmpeg_card_offers_what_is_written()
    {
        var card = new ResolveCardViewModel();
        card.Load(EngineSettings.Defaults with { Resolve = new ResolveSettings(@"D:\Resolve\Resolve.exe", "") });

        Assert.Equal((@"D:\Resolve\Resolve.exe", ""), (card.ResolveExe, card.ResolvePython));
        card.ResolvePython = @" D:\Resolve\py.exe ";
        Assert.Equal(new ResolveSettings(@"D:\Resolve\Resolve.exe", @"D:\Resolve\py.exe"), card.ApplyTo(EngineSettings.Defaults).Resolve);

        // OpenTimelineIO is not written yet: a saved choice of it reads as the FCPXML the Assembly writes.
        Assert.Equal([TimelineExport.Fcpxml, TimelineExport.None], FfmpegCardViewModel.TimelineExportChoices.Select(c => c.Value));
        var ffmpeg = new FfmpegCardViewModel();
        ffmpeg.Load(EngineSettings.Defaults with { Ffmpeg = new FfmpegSettings("ffmpeg", TimelineExport.OpenTimelineIo) });
        Assert.Equal(TimelineExport.Fcpxml, ffmpeg.TimelineExport);
    }

    [Fact]
    public async Task Settings_has_a_resolve_card_among_the_providers()
    {
        var page = new SettingsPageViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await page.LoadAsync();

        Assert.Contains(page.Cards, c => c is ResolveCardViewModel && c.Id == ProviderId.DavinciResolve);
        Assert.Same(page.Resolve, page.Cards[^1]);
    }
}
