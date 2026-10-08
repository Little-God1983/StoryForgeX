using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.Script;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>The voice in the result matrix: a Voice cell per row, the voice total, the panel's player and lit words.</summary>
public sealed class VoiceMatrixTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly FakePlayer _player = new();

    private static readonly DateTimeOffset At = new(2026, 10, 8, 18, 0, 0, TimeSpan.Zero);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> OpenAsync()
    {
        var profile = new ProfileRef(Guid.NewGuid(), 1);
        var project = await _client.CreateProjectAsync(new ProjectSetup(
            "Soul Coins – BG3 lore", "Soul coins in BG3.", ["bg3.wiki"],
            new WritingSetup("Claude CLI", "", profile, profile, profile),
            new VoiceSetup("ComfyUI", "Breeze TTS", profile),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", profile, Consistency.ReferenceImages, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", profile, 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 240, "English", AssemblyTarget.FfmpegWithFcpxml),
            [PipelineStage.Research, PipelineStage.Script, PipelineStage.Voice], RunMode.StopAtGates));
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [],
            [Segment("S01", "Hook"), Segment("S02", "Money of Hell"), Segment("S03", "Outro")], "", 232, 240, []);
        var matrix = new ResultMatrixPageViewModel(_client, _ => { }, () => _player);
        matrix.Show(project);
        await matrix.ScriptLoading;
        return (matrix, project);
    }

    private static SegmentView Segment(string id, string title) =>
        new(id, title, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)], $"Narration of {id}.", ["F01"], 77, null, []);

    private static VoiceSegmentView Voice(string id, double seconds, StageState state = StageState.NeedsReview, int version = 1, string? path = "default") =>
        new(id, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            path == "default" ? $@"C:\media\voice\{id}-v{version}.wav" : path,
            seconds,
            [new VoicePart("Soul coins scream.", seconds / 2), new VoicePart("They hold one soul.", seconds / 2)],
            [new SpokenWord("Soul", 0.15, 0.5), new SpokenWord("coins", 0.5, 1.0), new SpokenWord("scream.", 1.0, 1.6), new SpokenWord("They", 2.0, 2.3)],
            null, []);

    /// <summary>The voice was spoken and waits at its gate: S01 has two versions.</summary>
    private async Task SpokenAsync(ResultMatrixPageViewModel matrix, Project project, StageState state = StageState.NeedsReview)
    {
        _client.Voices[project.Id] = new VoiceView(project.Id, state, null, [],
            [Voice("S01", 31.2, version: 2), Voice("S02", 29, StageState.Approved), Voice("S03", 30)], 90.2, 240);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, state));
        await matrix.Updating;
    }

    [Fact]
    public async Task A_spoken_script_has_a_voice_cell_per_row_and_the_voice_total_beside_the_target()
    {
        var (matrix, project) = await OpenAsync();

        await SpokenAsync(matrix, project);

        var script = matrix.Script!;
        Assert.Equal(["review", "approved", "review"], script.Rows.Select(r => r.Voice.StateText));
        Assert.Equal(["0:31", "0:29", "0:30"], script.Rows.Select(r => r.Voice.LengthText));
        Assert.Equal("script about 3:52 · voice total 1:30 · target 4:00", script.LengthSummary);
        Assert.Equal("1 of 3 segments approved.", script.VoiceApprovedSummary);
        Assert.True(script.IsVoiceReview);
        Assert.False(script.ShowsScriptApproved);   // the voice's bar takes its place
        Assert.Equal("S02 voice: approved, 0:29", script.Rows[1].Voice.ToString());
    }

    [Fact]
    public async Task Before_the_voice_the_cells_cannot_be_opened_and_the_script_keeps_its_bar()
    {
        var (matrix, _) = await OpenAsync();

        var script = matrix.Script!;
        Assert.Equal("—", script.Rows[0].Voice.StateText);
        Assert.False(script.Rows[0].Voice.SelectCommand.CanExecute(null));
        Assert.True(script.ShowsScriptApproved);
        Assert.Equal("script about 3:52 · target 4:00", script.LengthSummary);
    }

    [Fact]
    public async Task While_the_voice_is_spoken_every_cell_says_running_and_the_bar_its_last_line()
    {
        var (matrix, project) = await OpenAsync();

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S02 part 1 of 2: sent to ComfyUI")));
        await matrix.Updating;

        var script = matrix.Script!;
        Assert.True(script.IsVoiceRunning);
        Assert.Equal("S02 part 1 of 2: sent to ComfyUI", script.VoiceProgress);
        Assert.All(script.Rows, r => Assert.Equal("running", r.Voice.StateText));
        Assert.True(script.CancelVoiceCommand.CanExecute(null));
    }

    [Fact]
    public async Task While_the_voice_is_spoken_a_segment_already_done_can_be_heard()
    {
        var (matrix, project) = await OpenAsync();
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S01 (1 of 3): Hook")));
        await matrix.Updating;

        // S01 is stored while S02 is spoken.
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Running, null, [], [Voice("S01", 31.2, StageState.Approved)], 31.2, 240);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Approved, Key: "S01"));
        await matrix.Updating;

        var script = matrix.Script!;
        Assert.Equal(["approved", "running", "running"], script.Rows.Select(r => r.Voice.StateText));
        Assert.True(script.IsVoiceRunning);
        Assert.Equal("script about 3:52 · voice total 0:31 · target 4:00", script.LengthSummary);
        script.Rows[0].Voice.SelectCommand.Execute(null);
        Assert.True(script.Playback.PlayPauseCommand.CanExecute(null));
        Assert.False(script.RegenerateSegmentCommand.CanExecute(null));   // not while the whole voice is spoken
    }

    [Fact]
    public async Task Speaking_the_whole_voice_again_shows_every_cell_running_at_once()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project, StageState.Approved);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S01 (1 of 3): Hook")));
        await matrix.Updating;

        Assert.All(matrix.Script!.Rows, r => Assert.Equal("running", r.Voice.StateText));
    }

    [Fact]
    public async Task Selecting_a_voice_cell_shows_its_words_its_versions_and_no_text_editing()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;

        script.Rows[0].Voice.SelectCommand.Execute(null);

        Assert.True(script.IsVoicePanel);
        Assert.True(script.Rows[0].Voice.IsSelected);
        Assert.False(script.Rows[0].IsSelected);
        Assert.Equal("S01 — Voice", script.SelectedHeading);
        Assert.Equal("review · version 2 · 0:31 · 2 parts", script.SelectedStatus);
        Assert.Equal(["v1", "v2"], script.SelectedVersions.Select(v => v.Label));
        Assert.Equal(["Soul", "coins", "scream.", "They"], script.Playback.Words.Select(w => w.Text));
        Assert.True(script.Playback.CanPlay);
        Assert.Equal("0:00 / 0:31", script.Playback.PositionText);
        Assert.Empty(script.SelectedFacts);
        Assert.False(script.StartEditCommand.CanExecute(null));
        Assert.True(script.ApproveSegmentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Playing_lights_the_word_being_heard_and_a_click_on_a_word_plays_from_there()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[0].Voice.SelectCommand.Execute(null);
        var playback = script.Playback;

        playback.PlayPauseCommand.Execute(null);
        _player.Tick(0.7);

        Assert.Equal(@"C:\media\voice\S01-v2.wav", _player.Opened);
        Assert.True(_player.Playing);
        Assert.Equal("Pause", playback.PlayLabel);
        Assert.Equal(["coins"], playback.Words.Where(w => w.IsCurrent).Select(w => w.Text));
        Assert.Equal("0:01 / 0:31", playback.PositionText);

        playback.Words[3].SeekCommand.Execute(null);
        Assert.Equal(2.0, _player.Position.TotalSeconds, 3);
        Assert.Equal(["They"], playback.Words.Where(w => w.IsCurrent).Select(w => w.Text));

        _player.End();
        Assert.DoesNotContain(playback.Words, w => w.IsCurrent);
        Assert.Equal("Play", playback.PlayLabel);
    }

    [Fact]
    public async Task Selecting_another_cell_stops_the_voice()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[0].Voice.SelectCommand.Execute(null);
        script.Playback.PlayPauseCommand.Execute(null);

        script.Rows[0].SelectCommand.Execute(null);

        Assert.True(_player.Closed);
        Assert.False(script.Playback.IsPlaying);
        Assert.Equal("S01 — Script", script.SelectedHeading);
    }

    [Fact]
    public async Task A_reload_of_the_same_voice_keeps_it_playing()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[0].Voice.SelectCommand.Execute(null);
        script.Playback.PlayPauseCommand.Execute(null);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Approved, Key: "S02"));
        await matrix.Updating;

        Assert.False(_player.Closed);
        Assert.True(script.Playback.IsPlaying);
    }

    [Fact]
    public async Task Approve_Regenerate_and_the_version_chips_act_on_the_voice()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[0].Voice.SelectCommand.Execute(null);

        await script.ApproveSegmentCommand.ExecuteAsync(null);
        await script.RegenerateSegmentCommand.ExecuteAsync(null);
        await script.ShowVersionCommand.ExecuteAsync(1);
        await script.ApproveRemainingVoiceCommand.ExecuteAsync(null);

        Assert.Equal([("approve", "S01", 2), ("regenerate", "S01", 0), ("select", "S01", 1), ("approve all", "", 0)], _client.VoiceActions);
        Assert.Empty(_client.ApprovedSegments);   // nothing went to the script
    }

    [Fact]
    public async Task A_failed_voice_says_why_and_Retry_speaks_it_again()
    {
        var (matrix, project) = await OpenAsync();
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Failed, "ComfyUI is not reachable at 127.0.0.1:8188.", [], [], 0, 240);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Failed));
        await matrix.Updating;
        var script = matrix.Script!;

        await script.RegenerateVoiceCommand.ExecuteAsync(null);

        Assert.True(script.IsVoiceFailed);
        Assert.Equal("ComfyUI is not reachable at 127.0.0.1:8188.", script.VoiceError);
        Assert.Equal("failed", script.Rows[0].Voice.StateText);
        Assert.Equal([(project.Id, PipelineStage.Voice)], _client.Regenerated);
    }

    [Fact]
    public async Task A_voice_whose_file_is_gone_says_so_and_cannot_play()
    {
        var (matrix, project) = await OpenAsync();
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [Voice("S01", 31, StageState.Approved, path: null)], 31, 240);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Approved));
        await matrix.Updating;
        var script = matrix.Script!;

        script.Rows[0].Voice.SelectCommand.Execute(null);

        Assert.True(script.Playback.IsMissing);
        Assert.False(script.Playback.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_voice_segments_log_line_shows_in_its_panel_not_the_scripts()
    {
        var (matrix, project) = await OpenAsync();
        await SpokenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[0].SelectCommand.Execute(null);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Running, new ActivityLine(At, ActivityKind.Model, "S01: sent to ComfyUI"), Key: "S01"));
        await matrix.Updating;
        Assert.Empty(script.SelectedActivity);

        script.Rows[0].Voice.SelectCommand.Execute(null);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Voice, StageState.Running, new ActivityLine(At, ActivityKind.Check, "S01: 0:31 of audio"), Key: "S01"));
        await matrix.Updating;
        Assert.Equal(["S01: 0:31 of audio"], script.SelectedActivity.Select(l => l.Text));
    }

    /// <summary>A player that plays nothing; the test moves its position and ends it.</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        public string? Opened { get; private set; }

        public bool Playing { get; private set; }

        public bool Closed { get; private set; }

        public TimeSpan Position { get; set; }

        public event EventHandler? Ticked;

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed { add { } remove { } }

        public void Open(string path) => (Opened, Closed, Position) = (path, false, TimeSpan.Zero);

        public void Play() => Playing = true;

        public void Pause() => Playing = false;

        public void Close() => (Closed, Playing) = (true, false);

        public void Tick(double seconds)
        {
            Position = TimeSpan.FromSeconds(seconds);
            Ticked?.Invoke(this, EventArgs.Empty);
        }

        public void End()
        {
            Playing = false;
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }
}
