using StoryForge.App.ViewModels.Assembly;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>
/// The Assembly screen (board 10): the video and its versions, one block per shot with the cut after
/// it marked, the selected cut's panel (move it, change its transition or its J/L), Render again with
/// your edits, and the gate: Approve, Regenerate, Retry, Cancel.
/// </summary>
public sealed class AssemblyScreenTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Id = Guid.Parse("5d1e0000-0000-0000-0000-000000000001");

    private static readonly AssemblyShot[] Shots =
    [
        new("S01/a", "S01·1", 0, 4.2, 0.5, "A coin that screams in the dark", @"C:\media\clips\a.mp4"),
        new("S01/b", "S01·2", 4.2, 10.3, 0.5, "Soul coins are minted in Avernus", @"C:\media\clips\b.mp4"),
        new("S02/a", "S02·1", 10.3, 15.3, 0.5, "The Blood War never ends", @"C:\media\clips\c.mp4"),
    ];

    private static CutList Claudes => new(
        [new Cut("S01/a", CutTransition.Crossfade, 0.4, 0, 0, "The title eases in."), new Cut("S01/b", CutTransition.Cut, 0, -0.2, 0.3, "The battle is heard first.")],
        -20, []);

    private static AssemblyView Review(CutList? list = null, StageState state = StageState.NeedsReview, CutList? waiting = null, string? error = null, int version = 1) =>
        new(Id, state, error, [], version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, v == 1 ? VersionOrigin.Generated : VersionOrigin.Edited, At, v == 1 ? null : 1))],
            new AssembledVideo(list ?? Claudes, @"media\assembly\video.mp4", 232.4, new GenerationSize("16:9", 1920, 1080), new Dictionary<string, int>(), new Dictionary<string, int>(), version == 1 ? null : 1),
            @"C:\media\assembly\video.mp4", Shots, true, waiting);

    private async Task<AssemblyViewModel> OpenAsync(AssemblyView view)
    {
        _client.Assembly[Id] = view;
        var screen = new AssemblyViewModel(_client, Id);
        await screen.LoadAsync();
        return screen;
    }

    [Fact]
    public async Task The_video_its_versions_and_one_block_per_shot_with_its_cut_marked()
    {
        var screen = await OpenAsync(Review());

        Assert.Equal(@"C:\media\assembly\video.mp4", screen.VideoPath);
        Assert.Equal("v1 · 3:52 · 1920×1080", screen.VideoInfo);
        Assert.Equal("v1 · 3:52 · 1920×1080. Approve it, or edit a cut and render again.", screen.BarText);
        Assert.Equal(["v1"], screen.Versions.Select(v => v.Label));
        Assert.Equal(["S01·1", "S01·2", "S02·1"], screen.Blocks.Select(b => b.Shot.Label));
        Assert.Equal(["◇ 0.4 s", "| · J 0.3", ""], screen.Blocks.Select(b => b.Mark));
        Assert.Equal([4.2, 6.1, 5], screen.Blocks.Select(b => Math.Round(b.Seconds, 2)));
        Assert.True(screen.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Picking_a_shot_shows_the_cut_after_it_and_goes_to_just_before_it()
    {
        var screen = await OpenAsync(Review());
        TimeSpan? seek = null;
        screen.SeekRequested += (_, at) => seek = at;

        screen.Blocks[1].SelectCommand.Execute(null);

        Assert.Equal("Cut 2 · S01·2 → S02·1", screen.CutHeading);
        Assert.Equal("“…are minted in Avernus” → “The Blood War never…”", screen.CutWords);   // four words each side
        Assert.Equal("0.2 s before the words", screen.ShiftText);
        Assert.Equal("J · 0.3 s before the picture: the next clip is heard first", screen.SoundText);
        Assert.Equal("The battle is heard first.", screen.CutWhy);
        Assert.Equal(TimeSpan.FromSeconds(8.8), seek);
        Assert.True(screen.Blocks[1].IsSelected);
        // The last shot has no cut after it: its own block picks the cut before it.
        screen.Blocks[2].SelectCommand.Execute(null);
        Assert.Equal("Cut 2 · S01·2 → S02·1", screen.CutHeading);
    }

    [Fact]
    public async Task Your_edits_mark_the_cut_and_Render_again_sends_them_while_Approve_waits()
    {
        var screen = await OpenAsync(Review());
        screen.Blocks[0].SelectCommand.Execute(null);

        screen.LaterCommand.Execute(null);
        screen.Transition = CutTransition.DipToBlack;
        screen.TransitionSeconds = "0.6";
        screen.ClipSound = "-30";

        Assert.Equal("0.1 s after the words", screen.ShiftText);
        Assert.Equal("■ 0.6 s", screen.Blocks[0].Mark);
        Assert.True(screen.Blocks[0].IsYours);
        Assert.False(screen.Blocks[1].IsYours);
        Assert.False(screen.ApproveCommand.CanExecute(null));   // approve what you see, not your unrendered edits
        await screen.RenderAgainCommand.ExecuteAsync(null);

        var (sent, from) = Assert.Single(_client.RenderedCutLists);
        Assert.Equal(1, from);   // the version you edited
        Assert.Equal(new Cut("S01/a", CutTransition.DipToBlack, 0.6, 0.1, 0, "The title eases in."), sent.Cuts[0]);
        Assert.Equal(Claudes.Cuts[1], sent.Cuts[1]);
        Assert.Equal(-30, sent.ClipSoundDb);
    }

    [Fact]
    public async Task Undo_brings_the_panel_back_to_the_cut_as_it_was_and_keeps_it_selected()
    {
        var screen = await OpenAsync(Review());
        screen.Blocks[1].SelectCommand.Execute(null);
        screen.Transition = CutTransition.Crossfade;

        screen.UndoCommand.Execute(null);

        Assert.Equal((CutTransition.Cut, "0", "0.3"), (screen.Transition, screen.TransitionSeconds, screen.SoundLead));
        Assert.True(screen.Blocks[1].IsSelected);
        screen.Transition = CutTransition.Crossfade;   // and it can be changed again at once
        Assert.Equal("◇ 0.4 s · J 0.3", screen.Blocks[1].Mark);
    }

    [Fact]
    public async Task With_an_older_version_approved_the_newest_still_opens()
    {
        // Approving v1 makes it the current one: v2 is no longer what "current" brings.
        var screen = await OpenAsync(Review(version: 2) with { Version = 1, ApprovedVersion = 1, State = StageState.Approved });
        _client.AssemblyVersions[(Id, 2)] = Review(version: 2) with { ApprovedVersion = 1, State = StageState.Approved };

        await screen.Versions[1].ShowCommand.ExecuteAsync(null);

        Assert.Equal(2, screen.Version);
    }

    [Fact]
    public async Task A_cut_list_cut_for_other_shots_cannot_be_edited_and_says_why()
    {
        // The storyboard was cut again since: three shots now, the video's list has one cut for two others.
        var old = new CutList([new Cut("S09/x", CutTransition.Cut, 0, 0, 0, "")], -20, []);

        var screen = await OpenAsync(Review(old) with { Stale = "A shot is gone since the video was cut." });

        Assert.False(screen.HasSelection);
        Assert.Equal("The shots changed since this video was cut: Regenerate cuts it again for them.", screen.EditBlocked);
        Assert.Equal(["", "", ""], screen.Blocks.Select(b => b.Mark));
        screen.Blocks[2].SelectCommand.Execute(null);
        Assert.Equal("", screen.CutHeading);
    }

    [Fact]
    public async Task A_hard_cut_has_no_length_and_a_crossfade_starts_at_0_4_s()
    {
        var screen = await OpenAsync(Review());
        screen.Blocks[1].SelectCommand.Execute(null);

        screen.Transition = CutTransition.Crossfade;

        Assert.Equal("0.4", screen.TransitionSeconds);
        Assert.Equal("◇ 0.4 s · J 0.3", screen.Blocks[1].Mark);
        screen.Transition = CutTransition.Cut;
        Assert.Equal("0", screen.TransitionSeconds);
        Assert.False(screen.HasEdits);   // back as Claude cut it
    }

    [Fact]
    public async Task A_cut_that_does_not_fit_says_why_and_a_number_that_is_not_one_too()
    {
        var screen = await OpenAsync(Review());
        screen.Blocks[0].SelectCommand.Execute(null);
        screen.SoundLead = "a lot";
        Assert.Equal("Give the sound change as a number, e.g. 0.4.", screen.EditProblem);
        screen.SoundLead = "0.9";
        _client.CutListRefusal = "S01·1 → S01·2: the sound changing 0.9 s before the picture runs past the clips' handles; make it change closer to the picture.";

        await screen.RenderAgainCommand.ExecuteAsync(null);

        Assert.Null(screen.EditProblem);
        Assert.Equal(_client.CutListRefusal, screen.Error);
        Assert.True(screen.HasEdits);   // still yours, to fix
    }

    [Fact]
    public async Task Retry_after_your_cut_list_failed_to_render_sends_it_again_and_otherwise_asks_Claude()
    {
        var mine = Claudes with { ClipSoundDb = -12 };
        var screen = await OpenAsync(Review(state: StageState.Failed, waiting: mine, error: "ffmpeg could not render the video: No space left on device."));

        Assert.Equal("-12", screen.ClipSound);   // yours comes back to edit
        await screen.RetryCommand.ExecuteAsync(null);

        Assert.Equal(-12, Assert.Single(_client.RenderedCutLists).CutList.ClipSoundDb);
        var claudes = await OpenAsync(Review(state: StageState.Failed, error: "The answer had no good cut list after 3 tries."));
        await claudes.RetryCommand.ExecuteAsync(null);
        Assert.Equal((Id, PipelineStage.Assembly), Assert.Single(_client.Regenerated));
    }

    [Fact]
    public async Task Retry_sends_your_cut_list_with_the_version_it_was_edited_from()
    {
        var screen = await OpenAsync(Review(state: StageState.Failed, waiting: Claudes with { ClipSoundDb = -12 }, version: 3) with { WaitingBasedOn = 1 });

        await screen.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, Assert.Single(_client.RenderedCutLists).EditedFrom);
    }

    [Fact]
    public async Task Cancel_while_your_cut_list_renders_keeps_your_edits_on_screen()
    {
        var mine = Claudes with { ClipSoundDb = -12 };
        var screen = await OpenAsync(Review(state: StageState.Running, waiting: mine));
        _client.Assembly[Id] = Review();   // what the engine shows once the render stopped: the version before, nothing waiting

        await screen.CancelCommand.ExecuteAsync(null);

        Assert.Equal((Id, PipelineStage.Assembly), Assert.Single(_client.Cancelled));
        Assert.Equal("-12", screen.ClipSound);
        Assert.True(screen.HasEdits);
        Assert.True(screen.RenderAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task Activity_lines_of_the_clips_do_not_reload_the_screen()
    {
        var screen = await OpenAsync(Review());
        _client.Assembly[Id] = Review(state: StageState.Stale);

        await screen.ReceiveAsync(new StageUpdate(Id, PipelineStage.Clips, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S02·1: 40%")));

        Assert.Equal(StageState.NeedsReview, screen.State);
    }

    [Fact]
    public async Task Regenerate_and_Approve_go_to_the_engine_and_an_older_version_can_be_shown()
    {
        var screen = await OpenAsync(Review(version: 2));
        _client.AssemblyVersions[(Id, 1)] = Review(version: 2) with { Version = 1 };

        await screen.ApproveCommand.ExecuteAsync(null);
        await screen.RegenerateCommand.ExecuteAsync(null);
        await screen.Versions[0].ShowCommand.ExecuteAsync(null);

        Assert.Equal((Id, PipelineStage.Assembly, 2), Assert.Single(_client.Approved));
        Assert.Equal((Id, PipelineStage.Assembly), Assert.Single(_client.Regenerated));
        Assert.Equal(["v1", "v2 · yours"], screen.Versions.Select(v => v.Label));
        Assert.Equal(1, screen.Version);
    }

    [Fact]
    public async Task Before_the_first_video_it_waits_for_the_clips_or_cuts_it_when_they_are_approved()
    {
        var waiting = await OpenAsync(new AssemblyView(Id, StageState.NotStarted, null, [], null, null, [], null, null, Shots, false, null));
        Assert.Equal("The video is cut once every clip is approved.", waiting.BarText);
        Assert.False(waiting.MakeVideoCommand.CanExecute(null));

        var ready = await OpenAsync(new AssemblyView(Id, StageState.NotStarted, null, [], null, null, [], null, null, Shots, true, null));
        await ready.MakeVideoCommand.ExecuteAsync(null);

        Assert.Equal((Id, PipelineStage.Assembly), Assert.Single(_client.Regenerated));
    }

    [Fact]
    public async Task The_Assembly_chip_opens_its_screen_in_place_of_the_matrix()
    {
        var project = await _client.CreateProjectAsync(StoryboardMatrixTests.Setup());
        _client.Assembly[project.Id] = Review() with { ProjectId = project.Id };
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;

        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages.Single(s => s.Stage == PipelineStage.Assembly));

        Assert.True(matrix.ShowsAssembly);
        Assert.False(matrix.ShowsMatrix);
        Assert.Equal(3, matrix.Assembly!.Blocks.Count);
        Assert.EndsWith("/ Assembly", matrix.Breadcrumb);
    }
}
