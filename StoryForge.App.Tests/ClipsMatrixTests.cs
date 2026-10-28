using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>
/// Board 2b's Clip column: each shot's Clip cell, the Clip panel (the video, its motion prompt, which
/// model made it, Regenerate, Edit prompt…, Make with… another video profile), and the Clips bar
/// with Approve remaining and Make clips.
/// </summary>
public sealed class ClipsMatrixTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly ClipModel Ltx = new(new ProfileRef(Guid.Parse("7a3f0000-0000-0000-0000-000000000001"), 2), "LTX 2.5");

    private static ClipView Made(string key, StageState state = StageState.NeedsReview, int version = 1, bool queued = false, string? error = null, Clip? clip = null) =>
        new(key, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            version == 0 ? null : clip ?? new Clip(key, $@"media\clips\{key.Replace('/', '-')}.mp4", "The camera pushes in on Karlach.", 184223,
                new GenerationSize("16:9", 1280, 704), 5.2, 0.5, "Minimax H3", "minimax.json", 2),
            null, "abc", queued, null, null, error, []);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> OpenAsync(ClipsView? clips = null, bool noClips = false)
    {
        var project = await _client.CreateProjectAsync(StoryboardMatrixTests.Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [],
            [StoryboardMatrixTests.Segment("S01", "Hook"), StoryboardMatrixTests.Segment("S02", "Outro")], "", 20, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [], 0, 60);
        _client.Storyboards[project.Id] = StoryboardMatrixTests.Planned(project.Id, StageState.Approved,
            StoryboardMatrixTests.Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved),
            StoryboardMatrixTests.Shot("S01", 2, 4.2, 10.3, 4, 6, StageState.Approved),
            StoryboardMatrixTests.Shot("S02", 1, 0, 5, 0, 10, StageState.Approved));
        _client.Stills[project.Id] = new StillsView(project.Id, StageState.Approved, null, [], [], true);
        if (noClips)
        {
            _client.Clips[project.Id] = new ClipsView(project.Id, StageState.NotStarted, null, [], [], true, "Minimax H3", []);
        }
        else
        {
            _client.Clips[project.Id] = clips ?? new ClipsView(project.Id, StageState.NeedsReview, null, [],
                [Made("S01/0001", StageState.Approved), Made("S01/0002", version: 2), Made("S02/0001", StageState.Failed, version: 0, error: "S02·1: CUDA out of memory")],
                true, "Minimax H3", [Ltx]);
        }
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;
        return (matrix, project);
    }

    [Fact]
    public async Task Each_shot_has_its_clip_cell_with_its_state_and_version()
    {
        var (matrix, _) = await OpenAsync();

        var shots = matrix.Script!.Rows.SelectMany(r => r.Shots).ToList();
        Assert.Equal(["approved", "review", "failed"], shots.Select(s => s.Clip.StateText));
        Assert.Equal(["v1", "v2", ""], shots.Select(s => s.Clip.VersionText));
        Assert.Equal("S01·2 clip: review, version 2", shots[1].Clip.AccessibleName);
    }

    [Fact]
    public async Task Before_the_clips_every_clip_cell_is_empty_and_the_bar_offers_to_make_them()
    {
        var (matrix, _) = await OpenAsync(noClips: true);
        var script = matrix.Script!;

        Assert.All(script.Rows.SelectMany(r => r.Shots), s => Assert.Equal(("—", false), (s.Clip.StateText, s.Clip.SelectCommand.CanExecute(null))));
        Assert.True(script.CanStartClips);

        await script.MakeClipsCommand.ExecuteAsync(null);
        Assert.Equal(("make", "", (string?)null), Assert.Single(_client.ClipActions));
    }

    [Fact]
    public async Task The_clip_panel_says_which_model_made_it_how_long_it_runs_and_its_prompt()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        script.Rows[0].Shots[1].Clip.SelectCommand.Execute(null);

        Assert.Equal(("S01·2 — Clip", "review · version 2"), (script.SelectedHeading, script.SelectedStatus));
        Assert.Equal("Made by Minimax H3 · from still v2", script.ClipMadeBy);
        Assert.Equal("5.2 s with 0.5 s handles · 1280×704 · seed 184223 · wf minimax.json", script.ClipDetails);
        Assert.Equal(("Motion prompt (from the storyboard + the video profile)", "The camera pushes in on Karlach."), (script.ClipPromptTitle, script.ClipPrompt));
        Assert.Equal(["v1", "v2"], script.SelectedVersions.Select(v => v.Label));
        Assert.False(script.RerunFromHereCommand.CanExecute(null));   // nothing comes after a clip yet
    }

    [Fact]
    public async Task A_failed_clip_says_why_and_Regenerate_puts_it_among_the_waiting_ones()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[1].Shots[0].Clip.SelectCommand.Execute(null);

        Assert.Equal("S02·1: CUDA out of memory", script.SelectedError);
        await script.RegenerateSegmentCommand.ExecuteAsync(null);

        Assert.Equal(("queue", "S02/0001", (string?)null), Assert.Single(_client.ClipActions));
    }

    [Fact]
    public async Task Make_with_another_video_profile_queues_that_one_clip_with_it()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[1].Shots[0].Clip.SelectCommand.Execute(null);
        Assert.Equal([Ltx], script.ClipModels);
        Assert.False(script.MakeWithCommand.CanExecute(null));   // pick one first

        script.SelectedClipModel = Ltx;
        await script.MakeWithCommand.ExecuteAsync(null);

        Assert.Equal(("queue", "S02/0001", $"profile {Ltx.Profile.ProfileId:N} v2"), Assert.Single(_client.ClipActions));
        Assert.Equal("Waits to be made again with LTX 2.5: Make clips makes it.", script.ClipQueuedText);
        Assert.Equal("S02·1 clip: failed, queued", script.Rows[1].Shots[0].Clip.AccessibleName);
    }

    [Fact]
    public async Task Edit_prompt_queues_the_clip_with_your_motion_prompt()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[0].Clip.SelectCommand.Execute(null);

        script.StartEditPromptCommand.Execute(null);
        Assert.Equal("The camera pushes in on Karlach.", script.EditPrompt);
        script.EditPrompt = "Karlach turns her head slowly.";
        await script.QueuePromptCommand.ExecuteAsync(null);

        Assert.Equal(("queue", "S01/0001", "Karlach turns her head slowly."), Assert.Single(_client.ClipActions));
        Assert.False(script.IsEditingPrompt);
    }

    [Fact]
    public async Task The_clips_bar_counts_what_is_approved_and_what_waits()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        Assert.Equal("1 of 3 shots approved.", script.ClipsApprovedSummary);
        Assert.Equal("1 failed: Make clips makes it.", script.ClipsQueuedSummary);
        Assert.True(script.MakeClipsCommand.CanExecute(null));
        await script.ApproveRemainingClipsCommand.ExecuteAsync(null);
        Assert.Equal(("approve all", "", (string?)null), Assert.Single(_client.ClipActions));
    }

    [Fact]
    public async Task Approving_a_clip_approves_the_version_shown()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.Rows[0].Shots[1].Clip.SelectCommand.Execute(null);

        await script.ApproveSegmentCommand.ExecuteAsync(null);

        Assert.Equal(("approve v2", "S01/0002", (string?)null), Assert.Single(_client.ClipActions));
    }

    [Fact]
    public async Task A_stale_clip_shows_stale_and_why()
    {
        var (matrix, _) = await OpenAsync(new ClipsView(Guid.Empty, StageState.Approved, null, [],
            [Made("S01/0001", StageState.Approved) with { Stale = "The still of S01·1 changed since the clip was made." }, Made("S01/0002", StageState.Approved), Made("S02/0001", StageState.Approved)],
            true, "Minimax H3", []));
        var script = matrix.Script!;

        script.Rows[0].Shots[0].Clip.SelectCommand.Execute(null);

        Assert.Equal("stale", script.Rows[0].Shots[0].Clip.StateText);
        Assert.Equal("Out of date: The still of S01·1 changed since the clip was made.", script.SelectedStale);
        Assert.False(script.HasClipModels);
    }
}
