using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>Board 2b: the shots sit under their segment, a segment folds, and the storyboard's bars and counts.</summary>
public sealed class StoryboardMatrixTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    internal static ProjectSetup Setup()
    {
        var profile = new ProfileRef(Guid.NewGuid(), 1);
        return new ProjectSetup(
            "Soul Coins – BG3 lore", "Soul coins in BG3.", ["bg3.wiki"],
            new WritingSetup("Claude CLI", "", profile, profile, profile),
            new VoiceSetup("ComfyUI", "Breeze TTS", profile),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", profile, Consistency.ReferenceImages, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", profile, 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 60, "English", AssemblyTarget.FfmpegWithFcpxml),
            [PipelineStage.Research, PipelineStage.Script, PipelineStage.Storyboard], RunMode.StopAtGates);
    }

    internal static SegmentView Segment(string id, string title) =>
        new(id, title, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)], $"Narration of {id}.", ["F01"], 10, null, []);

    internal static ShotView Shot(string segment, int number, double start, double end, int first, int count, StageState state = StageState.NeedsReview,
        string description = "A coin on a stone table.", int version = 1, bool continues = false, params string[] cast) =>
        new($"{segment}/{number:x4}", segment, number, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, v == 1 ? VersionOrigin.Generated : VersionOrigin.Edited, At, v == 1 ? null : v - 1))],
            new Shot($"{segment}/{number:x4}", segment, first, count, start, end, description, cast, continues), null, []);

    /// <summary>Ten words, one every 1.03 s: "w0" … "w9".</summary>
    internal static IReadOnlyList<SpokenWord> Words() => [.. Enumerable.Range(0, 10).Select(i => new SpokenWord($"w{i}", i * 1.03, i * 1.03 + 0.8))];

    /// <summary>S01 cut in two (4.2 s in review, 6.1 s approved), S02 one shot of 5 s in review.</summary>
    internal static StoryboardView Planned(Guid projectId, StageState state = StageState.NeedsReview, params ShotView[] shots) =>
        new(projectId, state, null, [],
            shots.Length > 0 ? shots : [Shot("S01", 1, 0, 4.2, 0, 4), Shot("S01", 2, 4.2, 10.3, 4, 6, StageState.Approved), Shot("S02", 1, 0, 5, 0, 10)],
            [new CastMember("Karlach", CastKind.Character, ""), new CastMember("Soul coin", CastKind.Object, ""), new CastMember("Raphael", CastKind.Character, "")],
            new Dictionary<string, IReadOnlyList<SpokenWord>> { ["S01"] = Words(), ["S02"] = Words() },
            Pacing.Default, 20);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> OpenAsync(StoryboardView? board = null)
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook"), Segment("S02", "Outro")], "", 20, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [], 0, 60);
        _client.Storyboards[project.Id] = board ?? Planned(project.Id);
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;
        return (matrix, project);
    }

    [Fact]
    public async Task The_shots_sit_under_their_segment_each_with_its_board_cell()
    {
        var (matrix, _) = await OpenAsync();

        var script = matrix.Script!;
        Assert.Equal(["S01·1", "S01·2"], script.Rows[0].Shots.Select(s => s.Label));
        Assert.Equal(["S02·1"], script.Rows[1].Shots.Select(s => s.Label));
        Assert.Equal(["review", "approved"], script.Rows[0].Shots.Select(s => s.StateText));
        Assert.Equal("S01·2 board: approved, version 1", script.Rows[0].Shots[1].AccessibleName);
        Assert.True(script.Rows[0].IsOpen);
        Assert.Equal("2 shots", script.Rows[0].ShotsText);
    }

    [Fact]
    public async Task A_folded_segment_shows_what_its_shots_need()
    {
        var (matrix, _) = await OpenAsync();
        var row = matrix.Script!.Rows[0];

        row.ToggleCommand.Execute(null);

        Assert.False(row.IsOpen);
        Assert.Equal("! 1  ✓ 1", row.BoardSummary);
        Assert.Equal("S01 shots folded: 1 review, 1 approved", row.BoardSummaryName);
    }

    [Fact]
    public async Task Fold_all_folds_every_segment_and_open_all_opens_them_again()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        Assert.True(script.ShowsFoldAll);
        Assert.Equal("Fold all", script.FoldAllLabel);

        script.FoldAllCommand.Execute(null);
        Assert.All(script.Rows, r => Assert.False(r.IsOpen));
        Assert.Equal("Open all", script.FoldAllLabel);

        script.FoldAllCommand.Execute(null);
        Assert.All(script.Rows, r => Assert.True(r.IsOpen));
    }

    [Fact]
    public async Task The_gate_bar_counts_the_shots_and_approves_the_rest()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        Assert.True(script.IsBoardReview);
        Assert.Equal("1 of 3 shots approved.", script.BoardApprovedSummary);
        await script.ApproveRemainingBoardCommand.ExecuteAsync(null);

        Assert.Equal(("approve all", ""), _client.ShotActions.Single());
    }

    [Fact]
    public async Task Regenerate_storyboard_plans_it_all_again()
    {
        var (matrix, project) = await OpenAsync();

        await matrix.Script!.RegenerateBoardCommand.ExecuteAsync(null);

        Assert.Equal((project.Id, PipelineStage.Storyboard), _client.Regenerated.Single());
    }

    [Fact]
    public async Task The_header_says_how_many_shots_stills_and_clips_it_makes_and_the_pacing_line_its_spread()
    {
        var (matrix, _) = await OpenAsync();

        Assert.EndsWith(" · 3 shots → 3 stills, 3 clips", matrix.Script!.LengthSummary);
        Assert.Equal("Pacing: shots of 3–8 s, about 5 s · 3 shots · shortest 4.2 s · longest 6.1 s · max clip 20 s", matrix.Script.PacingSummary);
    }

    [Fact]
    public async Task Before_the_storyboard_a_segment_has_no_shots_and_nothing_to_fold()
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook")], "", 10, 60, []);
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;

        var row = matrix.Script!.Rows[0];
        Assert.Empty(row.Shots);
        Assert.False(row.HasShots);
        Assert.False(matrix.Script.IsBoardReview);
        Assert.False(matrix.Script.ShowsFoldAll);
        Assert.DoesNotContain("shots", matrix.Script.LengthSummary);
    }

    [Fact]
    public async Task While_the_storyboard_is_planned_the_bar_shows_its_last_line()
    {
        var (matrix, project) = await OpenAsync(Planned(Guid.Empty, StageState.NotStarted, []) with { Shots = [] });

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Storyboard, StageState.Running, new ActivityLine(At, ActivityKind.Model, "planning the shots")));
        await matrix.Updating;

        var script = matrix.Script!;
        Assert.True(script.IsBoardRunning);
        Assert.Equal("planning the shots", script.BoardProgress);
        Assert.True(script.CancelBoardCommand.CanExecute(null));
        await script.CancelBoardCommand.ExecuteAsync(null);
        Assert.Equal((project.Id, PipelineStage.Storyboard), _client.Cancelled.Single());
    }

    [Fact]
    public async Task A_failed_storyboard_shows_why_with_retry()
    {
        var (matrix, project) = await OpenAsync(Planned(Guid.Empty) with { State = StageState.Failed, Error = "The answer was not a valid storyboard after 3 tries.", Shots = [] });

        var script = matrix.Script!;
        Assert.True(script.IsBoardFailed);
        Assert.Equal("The answer was not a valid storyboard after 3 tries.", script.BoardError);
        await script.RegenerateBoardCommand.ExecuteAsync(null);
        Assert.Equal((project.Id, PipelineStage.Storyboard), _client.Regenerated.Single());
    }

    [Fact]
    public async Task Selecting_a_board_cell_shows_that_shot_in_the_panel()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        var shot = script.Rows[0].Shots[1];

        shot.SelectCommand.Execute(null);

        Assert.Same(shot, script.SelectedShot);
        Assert.True(shot.IsSelected);
        Assert.False(script.Rows[0].IsSelected);
        Assert.True(script.IsBoardPanel);
        Assert.Equal("S01·2 — Board", script.SelectedHeading);
        Assert.Equal("approved · version 1 · 6.1 s", script.SelectedStatus);
    }

    [Fact]
    public async Task A_change_to_one_shot_reloads_the_rows_and_keeps_the_selection()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[0].SelectCommand.Execute(null);

        _client.Storyboards[project.Id] = Planned(project.Id, StageState.NeedsReview,
            Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved), Shot("S01", 2, 4.2, 10.3, 4, 6, StageState.Approved), Shot("S02", 1, 0, 5, 0, 10));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Storyboard, StageState.Approved, Key: "S01/0001"));
        await matrix.Updating;

        Assert.Equal("approved", script.Rows[0].Shots[0].StateText);
        Assert.Same(script.Rows[0].Shots[0], script.SelectedShot);
        Assert.Equal("2 of 3 shots approved.", script.BoardApprovedSummary);
    }

    [Fact]
    public async Task The_board_chip_opens_the_matrix()
    {
        var (matrix, _) = await OpenAsync();

        Assert.True(matrix.Stages.Single(s => s.Stage == PipelineStage.Storyboard).CanOpen);
    }

    [Fact]
    public async Task With_the_voice_approved_and_no_storyboard_yet_a_bar_offers_to_plan_it()
    {
        var (matrix, project) = await OpenAsync(Planned(Guid.Empty, StageState.NotStarted) with { Shots = [] });
        var script = matrix.Script!;

        Assert.True(script.CanPlanBoard);
        await script.RegenerateBoardCommand.ExecuteAsync(null);

        Assert.Equal((project.Id, PipelineStage.Storyboard), _client.Regenerated.Single());
    }

    [Fact]
    public async Task Before_the_voice_is_approved_there_is_nothing_to_plan()
    {
        var (matrix, project) = await OpenAsync(Planned(Guid.Empty, StageState.NotStarted) with { Shots = [] });
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.NeedsReview, null, [], [], 0, 60);
        await matrix.Script!.LoadAsync();

        Assert.False(matrix.Script.CanPlanBoard);
    }

    [Fact]
    public async Task The_voice_approval_still_shows_when_the_storyboard_starts_before_its_load_is_back()
    {
        var (matrix, project) = await OpenAsync(Planned(Guid.Empty, StageState.NotStarted) with { Shots = [] });
        var script = matrix.Script!;
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.NeedsReview, null, [], [], 0, 60);
        await script.LoadAsync();
        Assert.True(script.IsVoiceReview);

        // The voice is approved; the load that brings it waits, and the storyboard starts meanwhile.
        var gate = new TaskCompletionSource();
        _client.VoiceLoadGate = gate.Task;
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [], 0, 60);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Approved));
        var approving = matrix.Updating;
        _client.Storyboards[project.Id] = Planned(project.Id, StageState.Running) with { Shots = [] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Storyboard, StageState.Running));
        var starting = matrix.Updating;
        gate.SetResult();
        await approving;
        await starting;

        Assert.True(script.IsVoiceApproved);
        Assert.True(script.IsBoardRunning);
    }

    [Fact]
    public async Task When_the_whole_storyboard_is_planned_again_the_open_shot_cannot_be_changed()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[0].SelectCommand.Execute(null);
        Assert.True(script.CanEditBoard);
        // The screen only looks again when told: the panel's controls must hear of it.
        var told = 0;
        script.PropertyChanged += (_, e) => told += e.PropertyName == nameof(script.CanEditBoard) ? 1 : 0;
        var buttonTold = 0;
        script.EndLaterCommand.CanExecuteChanged += (_, _) => buttonTold++;

        _client.Storyboards[project.Id] = Planned(project.Id, StageState.Running);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Storyboard, StageState.Running));
        await matrix.Updating;

        Assert.False(script.CanEditBoard);
        Assert.False(script.EndLaterCommand.CanExecute(null));
        Assert.True(told > 0, "CanEditBoard was not raised");
        Assert.True(buttonTold > 0, "the arrows were not told");
    }

    [Fact]
    public async Task A_short_segment_offers_no_split_the_engine_would_refuse()
    {
        // S02 is 1.8 s: one shot, four words. Its halves would be under 2 s, and only a whole segment may be.
        IReadOnlyList<SpokenWord> shortWords = [new("Thanks", 0, 0.3), new("for", 0.45, 0.75), new("watching,", 0.9, 1.2), new("friends.", 1.35, 1.65)];
        var view = Planned(Guid.Empty, StageState.NeedsReview, Shot("S01", 1, 0, 4.2, 0, 4), Shot("S01", 2, 4.2, 10.3, 4, 6), Shot("S02", 1, 0, 1.8, 0, 4))
            with { Words = new Dictionary<string, IReadOnlyList<SpokenWord>> { ["S01"] = Words(), ["S02"] = shortWords } };
        var (matrix, _) = await OpenAsync(view);
        var script = matrix.Script!;

        script.Rows[1].Shots[0].SelectCommand.Execute(null);

        Assert.All(script.BoardWords, w => Assert.False(w.CanSplit));
        Assert.False(script.SplitAtCommand.CanExecute(2));
    }

    [Fact]
    public async Task Fold_all_counts_only_segments_with_shots_to_fold()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        script.FoldAllCommand.Execute(null);

        // A new script brings S03, which has no shots yet: nothing more to fold.
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook"), Segment("S02", "Outro"), Segment("S03", "New")], "", 30, 60, []);
        await script.LoadAsync();

        Assert.Equal("Open all", script.FoldAllLabel);
        script.FoldAllCommand.Execute(null);
        Assert.All(script.Rows.Where(r => r.HasShots), r => Assert.True(r.IsOpen));
        Assert.Equal("Fold all", script.FoldAllLabel);
    }
}
