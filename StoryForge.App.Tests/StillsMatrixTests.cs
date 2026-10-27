using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>
/// Board 2b's Still column: each shot's Still cell, the Still panel (picture, prompt, how it was made,
/// Regenerate into the batch, Edit prompt…, Replace with file…), and the Stills gate bar with
/// Approve remaining and Continue pipeline. And Test render on the Profiles screen.
/// </summary>
public sealed class StillsMatrixTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The picker "Replace with file…" opens: what it gives, null for none picked.</summary>
    private string? _picked = @"C:\Pictures\mine.png";

    private static StillView Made(string key, StageState state = StageState.NeedsReview, int version = 1, bool queued = false, string? queuedPrompt = null,
        Still? still = null) =>
        new(key, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            still ?? new Still(key, $@"media\stills\{key.Replace('/', '-')}.png", "Karlach in the crypt, painted dark fantasy", 7, new GenerationSize("16:9", 1344, 768), "qwen.json", ["R01/1"]),
            null, "abcdef1234567890", queued, queuedPrompt, null, []);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> OpenAsync(StillsView? stills = null, bool noStills = false)
    {
        var project = await _client.CreateProjectAsync(StoryboardMatrixTests.Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [],
            [StoryboardMatrixTests.Segment("S01", "Hook"), StoryboardMatrixTests.Segment("S02", "Outro")], "", 20, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [], 0, 60);
        _client.Storyboards[project.Id] = StoryboardMatrixTests.Planned(project.Id, StageState.Approved,
            StoryboardMatrixTests.Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved),
            StoryboardMatrixTests.Shot("S01", 2, 4.2, 10.3, 4, 6, StageState.Approved, continues: true),
            StoryboardMatrixTests.Shot("S02", 1, 0, 5, 0, 10, StageState.Approved));
        if (!noStills)
        {
            _client.Stills[project.Id] = stills ?? new StillsView(project.Id, StageState.NeedsReview, null, [],
                [Made("S01/0001", StageState.Approved), Made("S01/0002", version: 2, still: new Still("S01/0002", @"media\stills\b.png", "Karlach walks on", 9,
                    new GenerationSize("16:9", 1344, 768), "qwen.json", ["R01/1"], ContinuesFrom: "S01/0001")), Made("S02/0001")], true);
        }
        var matrix = new ResultMatrixPageViewModel(_client, _ => { }, pickPicture: () => _picked);
        matrix.Show(project);
        await matrix.ScriptLoading;
        return (matrix, project);
    }

    [Fact]
    public async Task Each_shot_has_its_still_cell_with_its_state_and_version()
    {
        var (matrix, _) = await OpenAsync();

        var shots = matrix.Script!.Rows.SelectMany(r => r.Shots).ToList();
        Assert.Equal(["approved", "review", "review"], shots.Select(s => s.Still.StateText));
        Assert.Equal(["v1", "v2", "v1"], shots.Select(s => s.Still.VersionText));
        Assert.Equal("S01·2 still: review, version 2", shots[1].Still.AccessibleName);
    }

    [Fact]
    public async Task Before_the_stills_every_still_cell_is_empty()
    {
        var (matrix, _) = await OpenAsync(noStills: true);

        Assert.All(matrix.Script!.Rows.SelectMany(r => r.Shots), s => Assert.Equal(("—", false), (s.Still.StateText, s.Still.SelectCommand.CanExecute(null))));
    }

    [Fact]
    public async Task A_folded_segment_shows_what_its_stills_need()
    {
        var (matrix, _) = await OpenAsync();
        var row = matrix.Script!.Rows[0];

        row.ToggleCommand.Execute(null);

        Assert.Equal("! 1  ✓ 1", row.StillSummary);
        Assert.Equal("S01 stills folded: 1 review, 1 approved", row.StillSummaryName);
    }

    [Fact]
    public async Task The_still_panel_shows_the_picture_its_prompt_and_how_it_was_made()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        script.Rows[0].Shots[1].Still.SelectCommand.Execute(null);

        Assert.True(script.IsStillPanel);
        Assert.True(script.Rows[0].Shots[1].Still.IsSelected);
        Assert.False(script.Rows[0].Shots[1].IsSelected);   // the Board cell is not the one selected
        Assert.Equal("S01·2 — Still", script.SelectedHeading);
        Assert.Equal("review · version 2", script.SelectedStatus);
        Assert.Equal("Karlach walks on", script.StillPrompt);
        Assert.Equal("Prompt (from the storyboard + the still profile)", script.StillPromptTitle);
        Assert.Equal("seed 9 · 1344×768 · wf qwen.json · inputs abcdef12…", script.StillDetails);
        Assert.Equal("Made from starts from the still of S01·1 (continues it) · reference images R01/1", script.StillMadeFrom);
        Assert.Equal(["v1", "v2"], script.SelectedVersions.Select(v => v.Label));
        // The file is not on this machine: the panel says it is gone.
        Assert.True(script.StillIsMissing);
    }

    [Fact]
    public async Task Approve_in_the_panel_approves_the_still_shown()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[1].Still.SelectCommand.Execute(null);

        await script.ApproveSegmentCommand.ExecuteAsync(null);

        Assert.Equal(("approve v2", "S01/0002", (string?)null), _client.StillActions.Single());
    }

    [Fact]
    public async Task Regenerate_on_a_still_puts_it_in_the_batch_and_Continue_pipeline_makes_it()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[1].Shots[0].Still.SelectCommand.Execute(null);
        Assert.False(script.ContinuePipelineCommand.CanExecute(null));   // nothing waits yet

        await script.RegenerateSegmentCommand.ExecuteAsync(null);

        Assert.Equal(("queue", "S02/0001", (string?)null), _client.StillActions.Single());
        Assert.Equal("queued", script.Rows[1].Shots[0].Still.VersionText);
        Assert.Equal("review · version 1 · queued", script.SelectedStatus);
        Assert.Equal("Waits in the batch: Continue pipeline makes it again.", script.StillQueuedText);
        Assert.Equal("1 still waits in the batch: Continue pipeline makes it.", script.QueuedSummary);
        Assert.True(script.ContinuePipelineCommand.CanExecute(null));

        await script.ContinuePipelineCommand.ExecuteAsync(null);

        Assert.Equal(("continue", "", (string?)null), _client.StillActions[^1]);
    }

    [Fact]
    public async Task Take_out_of_the_batch_leaves_the_still_as_it_is()
    {
        var (matrix, opened) = await OpenAsync();
        _client.Stills[opened.Id] = _client.Stills[opened.Id] with { Stills = [.. _client.Stills[opened.Id].Stills.Select(s => s.Key == "S02/0001" ? s with { Queued = true } : s)] };
        await matrix.Script!.LoadAsync();
        var script = matrix.Script!;
        script.Rows[1].Shots[0].Still.SelectCommand.Execute(null);
        Assert.True(script.StillIsQueued);

        await script.UnqueueStillCommand.ExecuteAsync(null);

        Assert.Equal(("unqueue", "S02/0001", (string?)null), _client.StillActions.Single());
        Assert.False(script.StillIsQueued);
    }

    [Fact]
    public async Task Edit_prompt_queues_the_still_with_your_prompt()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[0].Still.SelectCommand.Execute(null);

        script.StartEditPromptCommand.Execute(null);
        Assert.True(script.IsEditingPrompt);
        Assert.False(script.IsNotEditingAny);   // the panel's own buttons wait
        Assert.Equal("Karlach in the crypt, painted dark fantasy", script.EditPrompt);
        script.EditPrompt = "  Karlach laughing by a campfire  ";
        await script.QueuePromptCommand.ExecuteAsync(null);

        Assert.Equal(("queue", "S01/0001", "Karlach laughing by a campfire"), _client.StillActions.Single());
        Assert.False(script.IsEditingPrompt);
        Assert.Equal("Waits in the batch: Continue pipeline makes it again with your prompt.", script.StillQueuedText);
    }

    [Fact]
    public async Task Replace_with_file_puts_your_picture_in_and_no_pick_changes_nothing()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[0].Still.SelectCommand.Execute(null);

        await script.ReplaceStillCommand.ExecuteAsync(null);
        _picked = null;
        await script.ReplaceStillCommand.ExecuteAsync(null);

        Assert.Equal(("replace", "S01/0001", @"C:\Pictures\mine.png"), _client.StillActions.Single());
    }

    [Fact]
    public async Task Your_own_picture_says_so_and_has_no_seed()
    {
        var own = new Still("S02/0001", @"media\stills\own.png", "Karlach in the crypt", 0, new GenerationSize("16:9", 1920, 1080), "", [], YourFile: true);
        var (matrix, _) = await OpenAsync(new StillsView(Guid.Empty, StageState.NeedsReview, null, [], [Made("S02/0001", still: own)], true));
        var script = matrix.Script!;

        script.Rows[1].Shots[0].Still.SelectCommand.Execute(null);

        Assert.Equal("Your own picture (the prompt it stands in for)", script.StillPromptTitle);
        Assert.Equal("1920×1080 · inputs abcdef12…", script.StillDetails);
        Assert.Equal("", script.StillMadeFrom);
    }

    [Fact]
    public async Task The_gate_bar_counts_the_approved_stills_and_approves_the_rest()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        Assert.True(script.IsStillsReview);
        Assert.Equal("1 of 3 shots approved.", script.StillsApprovedSummary);
        await script.ApproveRemainingStillsCommand.ExecuteAsync(null);

        Assert.Equal(("approve all", "", (string?)null), _client.StillActions.Single());
    }

    [Fact]
    public async Task Approved_references_with_no_stills_yet_offer_to_make_them()
    {
        var (matrix, _) = await OpenAsync(new StillsView(Guid.Empty, StageState.NotStarted, null, [], [], ReferencesApproved: true));
        var script = matrix.Script!;

        Assert.True(script.CanMakeStills);
        await script.ContinuePipelineCommand.ExecuteAsync(null);

        Assert.Equal(("continue", "", (string?)null), _client.StillActions.Single());
    }

    [Fact]
    public async Task While_the_first_stills_are_made_each_cell_shows_running_until_it_is_done()
    {
        var (matrix, project) = await OpenAsync(new StillsView(Guid.Empty, StageState.NotStarted, null, [], [], ReferencesApproved: true));
        _client.Stills[project.Id] = _client.Stills[project.Id] with { ProjectId = project.Id };
        var script = matrix.Script!;

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Stills, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S01·1 (1 of 3)")));
        await matrix.Updating;

        Assert.True(script.IsStillsRunning);
        Assert.Equal("S01·1 (1 of 3)", script.StillsProgress);
        Assert.All(script.Rows.SelectMany(r => r.Shots), s => Assert.Equal(StageState.Running, s.Still.State));
        Assert.False(script.ContinuePipelineCommand.CanExecute(null));
    }

    [Fact]
    public async Task After_a_failed_run_Continue_pipeline_makes_the_shots_with_no_still_yet()
    {
        // The run made S01·1 and S01·2, then failed: S02·1 has no still.
        var (matrix, _) = await OpenAsync(new StillsView(Guid.Empty, StageState.Failed, "S02·1: CUDA out of memory", [],
            [Made("S01/0001"), Made("S01/0002")], true));
        var script = matrix.Script!;

        Assert.True(script.IsStillsFailed);
        Assert.Equal("1 shot has no still yet: Continue pipeline makes it.", script.QueuedSummary);
        await script.ContinuePipelineCommand.ExecuteAsync(null);

        Assert.Equal(("continue", "", (string?)null), _client.StillActions.Single());
    }

    [Fact]
    public async Task While_the_batch_is_made_only_its_stills_show_running()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        _client.Stills[project.Id] = _client.Stills[project.Id] with { Stills = [.. _client.Stills[project.Id].Stills.Select(s => s.Key == "S02/0001" ? s with { Queued = true } : s)] };
        await script.LoadAsync();

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Stills, StageState.Running));
        await matrix.Updating;

        Assert.Equal(["approved", "review", "running"], script.Rows.SelectMany(r => r.Shots).Select(s => s.Still.StateText));
        Assert.Equal("v1", script.Rows[1].Shots[0].Still.VersionText);   // running: no longer "queued"
    }

    [Fact]
    public async Task References_proposed_again_take_Continue_pipeline_away()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[1].Shots[0].Still.SelectCommand.Execute(null);
        await script.RegenerateSegmentCommand.ExecuteAsync(null);
        Assert.True(script.ContinuePipelineCommand.CanExecute(null));

        _client.Stills[project.Id] = _client.Stills[project.Id] with { ReferencesApproved = false };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.References, StageState.Running));
        await matrix.Updating;

        Assert.False(script.ContinuePipelineCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_Stills_chip_opens_the_matrix()
    {
        var (matrix, _) = await OpenAsync();
        await matrix.ShowReferencesCommand.ExecuteAsync(null);

        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages.Single(s => s.Stage == PipelineStage.Stills));

        Assert.True(matrix.ShowsMatrix);
    }

    [Fact]
    public async Task Test_render_makes_a_picture_from_the_test_line_and_the_profile_as_it_is()
    {
        _client.AddProfile(ProfileKind.Image, "Painted dark fantasy", FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI", PromptTemplate = "{shot.visual}" });
        var page = new ProfilesPageViewModel(_client);
        await page.LoadAsync();
        page.SelectedProfile = page.Profiles.Single();
        await page.Loading;
        var editor = page.Editor!;
        Assert.True(editor.CanTestRender);
        Assert.Equal("a lone knight on a cliff at dusk", editor.TestLine);

        editor.Draft.PromptTemplate = "{shot.visual}, oil paint";   // not saved: the test uses it all the same
        await editor.TestRenderCommand.ExecuteAsync(null);

        var (profile, line) = _client.TestRenders.Single();
        Assert.Equal(("{shot.visual}, oil paint", "a lone knight on a cliff at dusk"), (profile.PromptTemplate, line));
        Assert.Equal(@"C:\Data\test-renders\a.png", editor.TestPicture);
        Assert.Equal("seed 184223 · 1344×768 · 41 s", editor.TestDetails);
        Assert.Null(editor.TestError);
    }

    [Fact]
    public async Task Test_render_says_why_it_could_not_and_only_image_profiles_offer_it()
    {
        _client.AddProfile(ProfileKind.Image, "Painted dark fantasy", FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI" });
        _client.AddProfile(ProfileKind.Voice, "Calm narrator", FakeStoryForgeClient.EmptyContent);
        var page = new ProfilesPageViewModel(_client);
        await page.LoadAsync();
        page.SelectedProfile = page.Profiles.Single(p => p.Name == "Painted dark fantasy");
        await page.Loading;
        _client.StageFailure = new InvalidOperationException("The image profile has no workflow. Pick one in Profiles → Image.");

        await page.Editor!.TestRenderCommand.ExecuteAsync(null);

        Assert.Equal("The image profile has no workflow. Pick one in Profiles → Image.", page.Editor.TestError);
        Assert.Null(page.Editor.TestPicture);
        page.SelectedProfile = page.Profiles.Single(p => p.Name == "Calm narrator");
        await page.Loading;
        Assert.False(page.Editor.CanTestRender);
        Assert.False(page.Editor.TestRenderCommand.CanExecute(null));
    }
}
