using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>The whole script (board 2c): every segment in order, to read, approve or write again with a note.</summary>
public sealed class WholeScriptTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> OpenAsync()
    {
        var profile = new ProfileRef(Guid.NewGuid(), 1);
        var project = await _client.CreateProjectAsync(new ProjectSetup(
            "Soul Coins – BG3 lore", "Soul coins in BG3.", ["bg3.wiki"],
            new WritingSetup("Claude CLI", "", profile, profile, profile),
            new VoiceSetup("ComfyUI", "Breeze TTS", profile),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", profile, Consistency.ReferenceImages, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", profile, 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 60, "English", AssemblyTarget.FfmpegWithFcpxml),
            [PipelineStage.Research, PipelineStage.Script], RunMode.StopAtGates));
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.NeedsReview, null, [],
            [
                Segment("S01", "Hook – a coin that whispers", "Hold one, and strange whispers fill your mind.", 16, StageState.Approved),
                Segment("S02", "What they are", "In the Nine Hells, they are money.", 11),
                Segment("S03", "Outro", "A spell can set it free.", 5, version: 2),
            ],
            "", 32, 60, []);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.NeedsReview));
        await matrix.Updating;
        return (matrix, project);
    }

    private static SegmentView Segment(string id, string title, string narration, int seconds, StageState state = StageState.NeedsReview, int version = 1) =>
        new(id, title, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            narration, ["F01"], seconds, null, []);

    [Fact]
    public async Task Show_whole_script_opens_it_in_place_of_the_matrix_and_back_returns()
    {
        var (matrix, _) = await OpenAsync();

        matrix.ShowWholeScriptCommand.Execute(null);

        Assert.True(matrix.ShowsWholeScript);
        Assert.False(matrix.ShowsMatrix);
        Assert.True(matrix.ShowsStageScreen);
        Assert.Equal("Projects / Soul Coins – BG3 lore / Whole script", matrix.Breadcrumb);

        matrix.ShowMatrixCommand.Execute(null);

        Assert.False(matrix.ShowsWholeScript);
        Assert.True(matrix.ShowsMatrix);
    }

    [Fact]
    public async Task Every_segment_says_where_it_runs_how_long_it_is_and_its_words()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        Assert.Equal(["0:00–0:16 · 16 s · 8 words", "0:16–0:27 · 11 s · 7 words", "0:27–0:32 · 5 s · 6 words"], script.Rows.Select(r => r.Timing));
        Assert.Equal("3 segments · 0:32 · 21 words", script.WholeScriptTotals);
        Assert.Equal("In the Nine Hells, they are money.", script.Rows[1].Narration);
    }

    [Fact]
    public async Task Approve_on_a_segment_approves_the_version_it_shows()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;

        Assert.False(script.ApproveRowCommand.CanExecute(script.Rows[0]));   // approved already
        await script.ApproveRowCommand.ExecuteAsync(script.Rows[2]);

        Assert.Equal([(project.Id, "S03", 2)], _client.ApprovedSegments);
    }

    [Fact]
    public async Task Regenerate_on_a_segment_opens_the_note_line_there()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;

        script.AskNoteRowCommand.Execute(script.Rows[2]);

        Assert.Equal([false, false, true], script.Rows.Select(r => r.IsAskingNote));
        script.RegenerateNote = "mention Karlach earlier";
        await script.RegenerateSegmentCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, "S03")], _client.RegeneratedSegments);
        Assert.Equal(["mention Karlach earlier"], _client.RegenerateNotes);
        Assert.All(script.Rows, r => Assert.False(r.IsAskingNote));
    }

    [Fact]
    public async Task Edit_text_opens_the_editor_in_that_segment()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;

        script.StartEditRowCommand.Execute(script.Rows[1]);

        Assert.Equal([false, true, false], script.Rows.Select(r => r.IsEditing));
        Assert.Equal("In the Nine Hells, they are money.", script.EditNarration);
        script.EditNarration = "In the Nine Hells, souls are money.";
        await script.SaveEditCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, "S02", "What they are", "In the Nine Hells, souls are money.")], _client.EditedSegments);
        Assert.All(script.Rows, r => Assert.False(r.IsEditing));
    }

    [Fact]
    public async Task A_version_chip_switches_that_segment()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;

        Assert.Equal(["v1", "v2"], script.Rows[2].Versions.Select(v => v.Label));
        await script.ShowRowVersionCommand.ExecuteAsync(script.Rows[2].Versions[0]);

        Assert.Equal([(project.Id, "S03", 1)], _client.SelectedSegmentVersions);
    }

    [Fact]
    public async Task A_segment_being_written_again_cannot_be_approved_regenerated_or_edited()
    {
        var (matrix, project) = await OpenAsync();
        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with { Segments = [view.Segments[0], view.Segments[1] with { State = StageState.Running }, view.Segments[2]] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running, Key: "S02"));
        await matrix.Updating;
        var script = matrix.Script!;

        Assert.False(script.ApproveRowCommand.CanExecute(script.Rows[1]));
        Assert.False(script.AskNoteRowCommand.CanExecute(script.Rows[1]));
        Assert.False(script.StartEditRowCommand.CanExecute(script.Rows[1]));
        Assert.False(script.ShowRowVersionCommand.CanExecute(script.Rows[1].Versions[0]));
        Assert.True(script.AskNoteRowCommand.CanExecute(script.Rows[2]));
        Assert.True(script.ShowRowVersionCommand.CanExecute(script.Rows[2].Versions[0]));
    }

    [Fact]
    public async Task While_the_whole_script_is_written_again_nothing_on_it_can_be_used()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.Running));
        await matrix.Updating;

        Assert.False(script.ShowRowVersionCommand.CanExecute(script.Rows[2].Versions[0]));
        Assert.False(script.CopyAllCommand.CanExecute(null));   // it would copy the script being replaced
        Assert.False(script.HasSegments);
    }

    [Fact]
    public async Task The_segment_being_edited_keeps_your_text_until_Save_or_Cancel()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.StartEditRowCommand.Execute(script.Rows[1]);
        script.EditNarration = "In Hell, souls are money.";

        Assert.False(script.StartEditRowCommand.CanExecute(script.Rows[1]));
        Assert.False(script.AskNoteRowCommand.CanExecute(script.Rows[1]));
        Assert.False(script.ApproveRowCommand.CanExecute(script.Rows[1]));
        Assert.True(script.ApproveRowCommand.CanExecute(script.Rows[2]));

        script.CancelEditCommand.Execute(null);

        Assert.True(script.StartEditRowCommand.CanExecute(script.Rows[1]));
    }

    [Fact]
    public async Task While_one_segment_is_edited_the_others_cannot_open_a_note_or_an_editor()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.StartEditRowCommand.Execute(script.Rows[1]);
        script.EditNarration = "In Hell, souls are money.";

        Assert.False(script.AskNoteRowCommand.CanExecute(script.Rows[2]));    // it would close S02's editor, and your text with it
        Assert.False(script.StartEditRowCommand.CanExecute(script.Rows[2]));
        Assert.True(script.ApproveRowCommand.CanExecute(script.Rows[2]));     // leaves the editor alone

        script.CancelEditCommand.Execute(null);

        Assert.True(script.AskNoteRowCommand.CanExecute(script.Rows[2]));
    }

    [Fact]
    public async Task A_note_you_typed_is_not_dropped_by_opening_another()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.AskNoteRowCommand.Execute(script.Rows[1]);
        Assert.True(script.AskNoteRowCommand.CanExecute(script.Rows[2]));   // an empty note line is no loss

        script.RegenerateNote = "too long";

        Assert.False(script.AskNoteRowCommand.CanExecute(script.Rows[2]));
        Assert.False(script.StartEditRowCommand.CanExecute(script.Rows[1]));
        Assert.True(script.ApproveRowCommand.CanExecute(script.Rows[2]));

        script.CancelNoteCommand.Execute(null);

        Assert.True(script.AskNoteRowCommand.CanExecute(script.Rows[2]));
    }

    [Fact]
    public async Task An_open_shot_description_is_not_dropped_from_the_whole_script()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;

        script.IsEditingShot = true;   // left open in the Board panel before "Show whole script"

        Assert.False(script.AskNoteRowCommand.CanExecute(script.Rows[2]));
        Assert.False(script.StartEditRowCommand.CanExecute(script.Rows[2]));
    }

    [Fact]
    public async Task Copy_all_leaves_an_earlier_error_in_place()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.CopyText = _ => { };
        script.ActionError = "Could not approve the segment: S02 is being written.";

        script.CopyAllCommand.Execute(null);

        Assert.Equal("Could not approve the segment: S02 is being written.", script.ActionError);
    }

    [Fact]
    public async Task A_typed_note_keeps_its_segment_on_the_version_it_was_written_about()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.AskNoteRowCommand.Execute(script.Rows[2]);
        script.RegenerateNote = "too long";

        Assert.False(script.ShowRowVersionCommand.CanExecute(script.Rows[2].Versions[0]));
        Assert.False(script.ShowVersionCommand.CanExecute(1));   // the panel's chips, on the same segment

        script.CancelNoteCommand.Execute(null);

        Assert.True(script.ShowRowVersionCommand.CanExecute(script.Rows[2].Versions[0]));
    }

    [Fact]
    public async Task One_segment_reads_1_segment()
    {
        var (matrix, project) = await OpenAsync();
        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with { Segments = [view.Segments[0]] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.NeedsReview));
        await matrix.Updating;
        var script = matrix.Script!;
        script.CopyText = _ => { };

        script.CopyAllCommand.Execute(null);

        Assert.Equal("1 segment · 0:16 · 8 words", script.WholeScriptTotals);
        Assert.Equal("Copied: 1 segment, 8 words.", script.CopiedText);
    }

    [Fact]
    public async Task Writing_the_whole_script_again_waits_for_your_text()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        Assert.True(script.RegenerateScriptCommand.CanExecute(null));

        script.StartEditRowCommand.Execute(script.Rows[1]);
        Assert.False(script.RegenerateScriptCommand.CanExecute(null));   // it would throw your edit away
        script.CancelEditCommand.Execute(null);

        script.AskNoteRowCommand.Execute(script.Rows[2]);
        Assert.True(script.RegenerateScriptCommand.CanExecute(null));    // an empty note line is no loss
        script.RegenerateNote = "too long";
        Assert.False(script.RegenerateScriptCommand.CanExecute(null));

        script.CancelNoteCommand.Execute(null);
        Assert.True(script.RegenerateScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_copy_that_fails_does_not_say_Copied()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        script.CopyText = _ => { };
        script.CopyAllCommand.Execute(null);

        script.CopyText = _ => throw new System.Runtime.InteropServices.ExternalException("OpenClipboard failed.");
        script.CopyAllCommand.Execute(null);

        Assert.Equal("", script.CopiedText);
        Assert.Equal("Could not copy the script: OpenClipboard failed.", script.ActionError);
    }

    [Fact]
    public async Task Copy_all_copies_the_whole_narration()
    {
        var (matrix, _) = await OpenAsync();
        var script = matrix.Script!;
        string? copied = null;
        script.CopyText = text => copied = text;

        script.CopyAllCommand.Execute(null);

        var nl = Environment.NewLine;
        Assert.Equal($"Hold one, and strange whispers fill your mind.{nl}{nl}In the Nine Hells, they are money.{nl}{nl}A spell can set it free.", copied);
        Assert.Equal("Copied: 3 segments, 21 words.", script.CopiedText);
    }

    [Fact]
    public async Task Copied_goes_away_once_the_script_changes()
    {
        var (matrix, project) = await OpenAsync();
        var script = matrix.Script!;
        script.CopyText = _ => { };
        script.CopyAllCommand.Execute(null);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.NeedsReview, Key: "S01"));
        await matrix.Updating;
        Assert.Equal("Copied: 3 segments, 21 words.", script.CopiedText);   // the same script: what was copied still holds

        var view = _client.Scripts[project.Id];
        _client.Scripts[project.Id] = view with { Segments = [view.Segments[0], view.Segments[1] with { Narration = "In Hell, souls are money." }, view.Segments[2]] };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Script, StageState.NeedsReview, Key: "S02"));
        await matrix.Updating;

        Assert.Equal("", script.CopiedText);
    }
}
