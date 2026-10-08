using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>The script in the result matrix: rows per segment, the panel, approving, rewording, versions.</summary>
public sealed class ScriptMatrixTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 7, 15, 40, 0, TimeSpan.Zero);

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
            [PipelineStage.Research, PipelineStage.Script], RunMode.StopAtGates));
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;
        return (matrix, project);
    }

    private static SegmentView Segment(string id, string title, StageState state = StageState.NeedsReview, int version = 1, params string[] facts) =>
        new(id, title, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            $"Narration of {id}.", facts.Length > 0 ? facts : ["F01"], 48, null, []);

    /// <summary>The script was written: three segments wait for review.</summary>
    private async Task WrittenAsync(ResultMatrixPageViewModel matrix, Project project, string lengthNote = "")
    {
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.NeedsReview, null, [],
            [Segment("S01", "Hook – a coin that screams", facts: ["F01", "F02"]), Segment("S02", "Money of Hell", StageState.Approved), Segment("S03", "Outro", version: 2)],
            lengthNote, 232, 240,
            [new Fact("F01", "Soul coins hold one soul.", "https://bg3.wiki/wiki/Soul_Coin", "q", Weight: 10), new Fact("F02", "They are money in Hell.", "https://bg3.wiki/wiki/Soul_Coin", "q")]);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.NeedsReview));
        await matrix.Updating;
    }

    [Fact]
    public async Task A_written_script_shows_one_row_per_segment_and_the_first_in_the_panel()
    {
        var (matrix, project) = await OpenAsync();

        await WrittenAsync(matrix, project);

        var script = matrix.Script!;
        Assert.True(script.IsReview);
        Assert.True(script.HasSegments);
        Assert.Equal(["S01", "S02", "S03"], script.Rows.Select(r => r.Id));
        Assert.Equal(["review", "approved", "review"], script.Rows.Select(r => r.StateText));
        Assert.Equal("v2", script.Rows[2].VersionText);
        Assert.Equal("1 of 3 segments approved.", script.ApprovedSummary);
        Assert.Equal("script about 3:52 · target 4:00", script.LengthSummary);
        Assert.Equal("S01 — Script", script.SelectedHeading);
        Assert.Equal("review · version 1 · about 0:48", script.SelectedStatus);
        Assert.Equal("Narration of S01.", script.SelectedNarration);
        Assert.Equal([new("F01", 10, "Soul coins hold one soul."), new ViewModels.Script.UsedFact("F02", 5, "They are money in Hell.")], script.SelectedFacts);
        Assert.True(script.SelectedFacts[0].IsMust);
        Assert.Equal("Script – review", matrix.Stages[1].Label);
    }

    [Fact]
    public async Task A_script_longer_than_the_target_says_why()
    {
        var (matrix, project) = await OpenAsync();

        await WrittenAsync(matrix, project, "The facts marked must need about 5:10.");

        Assert.Equal("The facts marked must need about 5:10.", matrix.Script!.LengthNote);
    }

    [Fact]
    public async Task Selecting_a_cell_shows_that_segment_and_Approve_approves_its_version()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;

        script.Rows[2].SelectCommand.Execute(null);
        Assert.Equal("S03 — Script", script.SelectedHeading);
        Assert.True(script.Rows[2].IsSelected);
        Assert.False(script.Rows[0].IsSelected);
        Assert.Equal([new ViewModels.Script.SegmentVersionChoice(1, false, false), new(2, true, false)], script.SelectedVersions);

        await script.ApproveSegmentCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, "S03", 2)], _client.ApprovedSegments);
        Assert.Equal("approved", script.Rows[2].StateText);
        Assert.False(script.ApproveSegmentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Approve_remaining_approves_the_rest()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);

        await matrix.Script!.ApproveRemainingCommand.ExecuteAsync(null);

        Assert.Equal([project.Id], _client.ApprovedScripts);
        Assert.True(matrix.Script.IsApproved);
        Assert.False(matrix.Script.ApproveRemainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task Approve_remaining_settles_a_script_whose_segments_are_all_approved()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with { Segments = [.. view.Segments.Select(s => s with { State = StageState.Approved, ApprovedVersion = s.Version })] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Approved, Key: "S03"));
        await matrix.Updating;

        Assert.True(matrix.Script!.IsReview);
        Assert.True(matrix.Script.ApproveRemainingCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_segment_is_reworded_in_the_panel_as_a_new_version()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;

        script.StartEditCommand.Execute(null);
        Assert.Equal(("Hook – a coin that screams", "Narration of S01."), (script.EditTitle, script.EditNarration));
        script.EditNarration = " ";
        Assert.False(script.SaveEditCommand.CanExecute(null));
        script.EditNarration = " A coin screams in the dark. ";
        await script.SaveEditCommand.ExecuteAsync(null);

        Assert.False(script.IsEditing);
        Assert.Equal((project.Id, "S01", "Hook – a coin that screams", "A coin screams in the dark."), _client.EditedSegments.Single());
        Assert.Equal("A coin screams in the dark.", script.SelectedNarration);
        Assert.Equal("v2", script.Rows[0].VersionText);
    }

    [Fact]
    public async Task A_rewording_that_does_not_go_through_stays_in_the_editor()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;
        script.StartEditCommand.Execute(null);
        script.EditNarration = "A long new narration.";
        _client.StageFailure = new InvalidOperationException("S01 is being written. Try again when it is done.");

        await script.SaveEditCommand.ExecuteAsync(null);

        Assert.True(script.IsEditing);
        Assert.Equal("A long new narration.", script.EditNarration);
        Assert.StartsWith("Could not save the segment", script.ActionError);
    }

    [Fact]
    public async Task A_version_chip_switches_the_segment_to_that_version()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[2].SelectCommand.Execute(null);

        await script.ShowVersionCommand.ExecuteAsync(1);

        Assert.Equal([(project.Id, "S03", 1)], _client.SelectedSegmentVersions);
        Assert.Equal("v1", script.Rows[2].VersionText);
    }

    [Fact]
    public async Task Regenerate_writes_the_selected_segment_again()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);

        await matrix.Script!.RegenerateSegmentCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, "S01")], _client.RegeneratedSegments);
    }

    [Fact]
    public async Task A_segment_written_again_shows_its_log_in_the_panel_and_Cancel_stops_it()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;
        script.Rows[1].SelectCommand.Execute(null);
        Assert.False(script.SelectedShowsLog);
        Assert.False(script.CancelSegmentCommand.CanExecute(null));

        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with
        {
            Segments = [.. view.Segments.Select(s => s.Id == "S02" ? s with { State = StageState.Running, Activity = [new ActivityLine(At, ActivityKind.Model, "rewriting S02")] } : s)],
        };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, Key: "S02"));
        await matrix.Updating;
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, new ActivityLine(At, ActivityKind.Check, "S02 rewritten"), Key: "S02"));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, new ActivityLine(At, ActivityKind.Check, "S03 line"), Key: "S03"));
        await matrix.Updating;

        Assert.True(script.SelectedShowsLog);
        Assert.Equal(["rewriting S02", "S02 rewritten"], script.SelectedActivity.Select(a => a.Text));
        Assert.DoesNotContain(script.Activity, a => a.Text.StartsWith('S'));
        Assert.Equal("Script – review", matrix.Stages[1].Label);

        await script.CancelSegmentCommand.ExecuteAsync(null);
        Assert.Equal([(project.Id, "S02")], _client.CancelledSegments);
    }

    [Fact]
    public async Task A_load_with_an_older_log_does_not_take_lines_from_the_segment_shown()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        var script = matrix.Script!;
        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with { Segments = [.. view.Segments.Select(s => s.Id == "S01" ? s with { State = StageState.Running } : s)] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, Key: "S01"));
        await matrix.Updating;

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, new ActivityLine(At, ActivityKind.Model, "asking Claude"), Key: "S01"));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, new ActivityLine(At, ActivityKind.Check, "checking"), Key: "S01"));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Approved, Key: "S03"));   // its load still has no lines of S01
        await matrix.Updating;

        Assert.Equal(["asking Claude", "checking"], script.SelectedActivity.Select(a => a.Text));
    }

    [Fact]
    public async Task While_the_script_is_written_its_log_shows_and_Cancel_stops_it()
    {
        var (matrix, project) = await OpenAsync();

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, new ActivityLine(At, ActivityKind.Model, "writing the script")));
        await matrix.Updating;

        var script = matrix.Script!;
        Assert.True(script.IsRunning);
        Assert.True(script.ShowsLog);
        Assert.Equal(["writing the script"], script.Activity.Select(a => a.Text));
        Assert.Equal("Script – running", matrix.Stages[1].Label);

        await script.CancelCommand.ExecuteAsync(null);
        Assert.Equal([(project.Id, PipelineStage.Script)], _client.Cancelled);
    }

    [Fact]
    public async Task A_failed_script_says_why_and_Retry_writes_it_again()
    {
        var (matrix, project) = await OpenAsync();
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Failed, "The answer was not a valid script after 3 tries. Last problem: S01 uses F99.", [], [], "", 0, 240, []);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Failed));
        await matrix.Updating;

        var script = matrix.Script!;
        Assert.True(script.IsFailed);
        Assert.True(script.ShowsLog);
        Assert.StartsWith("The answer was not a valid script", script.Error);

        await script.RegenerateScriptCommand.ExecuteAsync(null);
        Assert.Equal([(project.Id, PipelineStage.Script)], _client.Regenerated);
    }

    [Fact]
    public async Task An_action_that_does_not_go_through_says_why()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);
        _client.StageFailure = new InvalidOperationException("S01 is being written. Try again when it is done.");

        await matrix.Script!.ApproveSegmentCommand.ExecuteAsync(null);

        Assert.Equal("Could not approve the segment: S01 is being written. Try again when it is done.", matrix.Script.ActionError);
    }

    [Fact]
    public async Task A_segments_own_update_leaves_the_stage_chips_alone()
    {
        var (matrix, project) = await OpenAsync();
        await WrittenAsync(matrix, project);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, Key: "S02"));
        await matrix.Updating;

        Assert.Equal("Script – review", matrix.Stages[1].Label);
    }

    [Fact]
    public async Task Approving_the_fact_sheet_goes_back_to_the_matrix_and_the_Script_chip_opens_it()
    {
        var (matrix, project) = await OpenAsync();
        _client.FactSheets[project.Id] = new FactSheetView(project.Id, StageState.NeedsReview, null,
            [new ResultVersion(1, VersionOrigin.Generated, At, null)], 1, null,
            new FactSheet([new Fact("F01", "Soul coins hold one soul.", "https://bg3.wiki/wiki/Soul_Coin", "q")]), []);
        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages[0]);
        Assert.True(matrix.ShowsFactSheet);

        await matrix.FactSheet!.ApproveCommand.ExecuteAsync(null);

        Assert.True(matrix.ShowsMatrix);
        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages[0]);
        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages[1]);
        Assert.True(matrix.ShowsMatrix);
        Assert.True(matrix.Stages[1].CanOpen);
    }
}
