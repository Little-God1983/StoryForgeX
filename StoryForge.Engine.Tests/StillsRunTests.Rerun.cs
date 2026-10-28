using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryForge.Client;
using StoryForge.Engine.References;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Tests;

/// <summary>
/// Rerun pipeline from here: one cell made again, then stage by stage what is made from it and
/// nothing else, each waiting at its gate; a segment's shots cut again with the others left as they are.
/// </summary>
public sealed partial class StillsRunTests
{
    /// <summary>One segment cut again: 10 shots of 15 words, Raphael new on the cast list, Karlach spelled otherwise.</summary>
    private static StoryboardOutput Recut(string id) => new(
        [new("karlach", "character", ""), new("Raphael", "character", "")],
        [new SegmentShotsOutput(id, [.. Enumerable.Range(0, 10).Select(i => new ShotOutput(i * 15, $"{id} new shot {i + 1}", i == 0 ? ["Raphael"] : ["karlach"], false))])]);

    /// <summary>Ticks a gate of the project, as if it was set up with it.</summary>
    private async Task GateAsync(Guid projectId, PipelineStage stage)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        await using var db = await _engine.DbAsync();
        var entry = await db.Projects.SingleAsync(p => p.Id == projectId);
        var setup = JsonSerializer.Deserialize<ProjectSetup>(entry.SetupJson, options)!;
        entry.SetupJson = JsonSerializer.Serialize(setup with { Gates = [.. setup.Gates, stage] }, options);
        await db.SaveChangesAsync();
    }

    private static Task<StoryboardView> CutAsync(IStoryForgeClient client, Guid projectId, string segmentId, int shots) =>
        ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(projectId),
            v => v.State == StageState.Failed || (v.State != StageState.Running && v.Shots.Count(s => s.SegmentId == segmentId) == shots));

    [Fact]
    public async Task A_rerun_from_a_voice_makes_its_voice_shots_and_stills_again_and_nothing_else()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        var made = await StillsAsync(client, project.Id);
        var others = (await client.GetStoryboardAsync(project.Id)).Shots.Where(s => s.SegmentId != "S02").Select(s => (s.Key, s.Version, s.State)).ToList();
        var otherStills = made.Stills.Where(s => !s.Key.StartsWith("S02/", StringComparison.Ordinal)).Select(s => (s.Key, s.Version)).ToList();
        Assert.Equal(
            "This remakes S02's voice, its 15 shots and their 15 stills. Other segments stay as they are. It waits at each gate for your approval, as a run does.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Voice, "S02")).Text);
        _board.Answer(Recut("S02"));
        var runs = StillRuns().Count;

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        // No Voice gate here: the shots are cut again at once, and wait at the storyboard's gate.
        var board = await CutAsync(client, project.Id, "S02", 10);
        Assert.Null(board.Error);
        Assert.Equal((2, false), (await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02") is var voice ? (voice.Version, voice.Rerun) : default);
        var cut = board.Shots.Where(s => s.SegmentId == "S02").ToList();
        Assert.All(cut, s => Assert.Equal((StageState.NeedsReview, true), (s.State, s.Rerun)));
        Assert.Equal("S02 new shot 1", cut[0].Shot.Description);
        Assert.Equal(others, board.Shots.Where(s => s.SegmentId != "S02").Select(s => (s.Key, s.Version, s.State)));
        // The model cut S02 alone, knowing the cast so far; the list keeps everyone and takes in Raphael.
        var request = _board.Requests[^1];
        Assert.Equal(["S02"], request.Segments.Select(s => s.Segment.Id));
        Assert.Equal(["Karlach", "Soul coin", "Mammon's mint"], request.Cast!.Select(c => c.Name));
        Assert.Equal(["Karlach", "Soul coin", "Mammon's mint", "Raphael"], board.Cast.Select(c => c.Name));
        Assert.Equal(["Karlach"], cut[1].Shot.Cast);   // as the list spells her
        Assert.All(board.Shots, s => Assert.Null(s.Stale));
        // The old stills went with the old shots; the new ones wait for the shots to be approved.
        Assert.Equal(otherStills, (await client.GetStillsAsync(project.Id)).Stills.Select(s => (s.Key, s.Version)));
        Assert.Equal(runs, StillRuns().Count);

        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);

        var stills = await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Count == 55);
        Assert.Null(stills.Error);
        Assert.Equal(otherStills, stills.Stills.Where(s => !s.Key.StartsWith("S02/", StringComparison.Ordinal)).Select(s => (s.Key, s.Version)));
        Assert.Equal(cut.Select(s => s.Key).Order(StringComparer.Ordinal), stills.Stills.Where(s => s.Key.StartsWith("S02/", StringComparison.Ordinal)).Select(s => s.Key));
        Assert.Equal(runs + 10, StillRuns().Count);
        Assert.All((await client.GetStoryboardAsync(project.Id)).Shots, s => Assert.False(s.Rerun));
    }

    [Fact]
    public async Task With_the_voice_gate_the_new_voice_waits_for_approval_before_the_shots_are_cut()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await GateAsync(project.Id, PipelineStage.Voice);
        _board.Answer(Recut("S02"));
        var asked = _board.Requests.Count;

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        var voice = await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments.Single(s => s.Id == "S02").State is StageState.NeedsReview or StageState.Failed);
        Assert.Equal((2, StageState.NeedsReview, true), voice.Segments.Single(s => s.Id == "S02") is var s02 ? (s02.Version, s02.State, s02.Rerun) : default);
        Assert.Equal(15, (await client.GetStoryboardAsync(project.Id)).Shots.Count(s => s.SegmentId == "S02"));
        Assert.Equal(asked, _board.Requests.Count);

        await client.ApproveSegmentAsync(project.Id, PipelineStage.Voice, "S02", 2);

        Assert.Null((await CutAsync(client, project.Id, "S02", 10)).Error);
        Assert.Equal(asked + 1, _board.Requests.Count);
    }

    [Fact]
    public async Task Stop_rerun_leaves_what_waits_as_it_is_and_makes_nothing_after_it()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await GateAsync(project.Id, PipelineStage.Voice);
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments.Single(s => s.Id == "S02").State == StageState.NeedsReview);

        await client.StopRerunAsync(project.Id);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Voice, "S02", 2);

        var board = await client.GetStoryboardAsync(project.Id);
        Assert.NotEqual(StageState.Running, board.State);   // approving went on with nothing
        Assert.Equal(15, board.Shots.Count(s => s.SegmentId == "S02"));
        Assert.False((await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02").Rerun);
    }

    [Fact]
    public async Task Retry_after_a_failed_cut_cuts_that_segment_again_not_the_whole_storyboard()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var others = (await client.GetStoryboardAsync(project.Id)).Shots.Where(s => s.SegmentId != "S02").Select(s => (s.Key, s.Version)).ToList();
        var wrong = Recut("S02") with { Segments = [new SegmentShotsOutput("S03", Recut("S02").Segments[0].Shots)] };
        _board.Answer(wrong).Answer(wrong).Answer(wrong);

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        var failed = await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.Failed);
        Assert.StartsWith("The answer was not a valid storyboard after 3 tries.", failed.Error);
        Assert.Equal(15, failed.Shots.Count(s => s.SegmentId == "S02"));   // the old shots stay until new ones are cut
        Assert.True((await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02").Rerun);
        _board.Answer(Recut("S02"));

        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);

        var board = await CutAsync(client, project.Id, "S02", 10);
        Assert.Equal(StageState.NeedsReview, board.State);
        Assert.Equal(["S02"], _board.Requests[^1].Segments.Select(s => s.Segment.Id));
        Assert.Equal(others, board.Shots.Where(s => s.SegmentId != "S02").Select(s => (s.Key, s.Version)));
    }

    [Fact]
    public async Task A_rerun_from_your_edit_of_a_segment_keeps_it_and_speaks_it_again()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var s02 = (await client.GetScriptAsync(project.Id)).Segments.Single(s => s.Id == "S02");
        await client.EditSegmentAsync(project.Id, "S02", s02.Title + " again", s02.Narration);
        var edited = (await client.GetScriptAsync(project.Id)).Segments.Single(s => s.Id == "S02");
        var written = _script.RewriteRequests.Count;
        Assert.Equal(
            "This keeps your edit of S02's script and remakes its voice, its 15 shots and their 15 stills. Other segments stay as they are. It waits at each gate for your approval, as a run does.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Script, "S02")).Text);
        _board.Answer(Recut("S02"));

        await client.RerunFromHereAsync(project.Id, PipelineStage.Script, "S02");

        Assert.Null((await CutAsync(client, project.Id, "S02", 10)).Error);
        Assert.Equal(edited.Version, (await client.GetScriptAsync(project.Id)).Segments.Single(s => s.Id == "S02").Version);
        Assert.Equal(written, _script.RewriteRequests.Count);
        Assert.Equal(2, (await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02").Version);
    }

    [Fact]
    public async Task A_rerun_from_a_reference_image_makes_only_the_stills_that_use_it_and_those_starting_from_them()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        var made = await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        Assert.Equal(
            "This remakes the 4 stills that use this image of Soul coin and the 4 that start from them. Other stills stay as they are.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.References, "R02/1")).Text);
        var runs = StillRuns().Count;

        await client.RerunFromHereAsync(project.Id, PipelineStage.References, "R02/1");

        var stills = await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Count(s => s.Version == 2) == 8);
        var again = new[] { "S01·1", "S01·2", "S02·1", "S02·2", "S03·1", "S03·2", "S04·1", "S04·2" }.Select(l => keys[l]).ToHashSet();
        Assert.All(stills.Stills, s => Assert.Equal(again.Contains(s.Key) ? 2 : 1, s.Version));
        Assert.Equal(runs + 8, StillRuns().Count);
        Assert.Equal(made.Stills.Count, stills.Stills.Count);
    }

    [Fact]
    public async Task A_cut_whose_cast_list_names_only_who_is_new_still_passes()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        // As the prompt asks: Raphael, new, on the list; Karlach, on it already, only in the shots.
        _board.Answer(Recut("S02") with { Cast = [new("Raphael", "character", "")] });

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        var board = await CutAsync(client, project.Id, "S02", 10);
        Assert.Null(board.Error);
        Assert.Equal(["Karlach", "Soul coin", "Mammon's mint", "Raphael"], board.Cast.Select(c => c.Name));
        Assert.Equal(["Karlach"], board.Shots.First(s => s.SegmentId == "S02" && s.Number == 2).Shot.Cast);
    }

    [Fact]
    public async Task Cancelling_a_rerun_still_waiting_its_turn_stops_it()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        _board.HoldShot();
        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, keys["S01·1"]);   // runs, and holds the queue
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => _board.Rewrites.Count == 1);

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await client.CancelSegmentAsync(project.Id, PipelineStage.Voice, "S02");

        var voice = (await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02");
        Assert.Equal((1, StageState.Approved, false), (voice.Version, voice.State, voice.Rerun));
        await client.CancelSegmentAsync(project.Id, PipelineStage.Storyboard, keys["S01·1"]);
    }

    [Fact]
    public async Task Stop_rerun_while_the_shots_are_cut_again_leaves_their_stills_alone()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var release = new TaskCompletionSource();
        _board.AnswerWhen(release.Task, Recut("S02"));
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.Running);

        await client.StopRerunAsync(project.Id);
        release.SetResult();

        var board = await CutAsync(client, project.Id, "S02", 10);
        Assert.All(board.Shots, s => Assert.False(s.Rerun));
    }

    [Fact]
    public async Task Retry_after_the_whole_storyboard_failed_plans_it_whole_even_with_a_rerun_waiting()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await GateAsync(project.Id, PipelineStage.Voice);
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments.Single(s => s.Id == "S02").State == StageState.NeedsReview);
        var bad = Board() with { Segments = [.. Board().Segments.Take(3)] };
        _board.Answer(bad).Answer(bad).Answer(bad);
        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.Failed);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Voice, "S02", 2);   // the rerun waits for the storyboard
        _board.Answer(Board());

        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);

        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State is StageState.NeedsReview or StageState.Failed);
        var board = await client.GetStoryboardAsync(project.Id);
        Assert.Equal(4, _board.Requests[^1].Segments.Count);
        // S02 was cut from its new voice with the rest: the rerun goes on from its new shots, and is not cut again.
        Assert.All(board.Shots, s => Assert.Equal(s.SegmentId == "S02", s.Rerun));
        Assert.False((await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S02").Rerun);
    }

    [Fact]
    public async Task Retry_after_a_failed_cut_cuts_that_segment_again_even_after_Stop_rerun()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var wrong = Recut("S02") with { Segments = [new SegmentShotsOutput("S03", Recut("S02").Segments[0].Shots)] };
        _board.Answer(wrong).Answer(wrong).Answer(wrong);
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.Failed);
        await client.StopRerunAsync(project.Id);
        _board.Answer(Recut("S02"));

        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);

        var board = await CutAsync(client, project.Id, "S02", 10);
        Assert.Equal(["S02"], _board.Requests[^1].Segments.Select(s => s.Segment.Id));
        Assert.All(board.Shots, s => Assert.False(s.Rerun));   // stopped: it goes no further
    }

    [Fact]
    public async Task One_rerun_waiting_at_its_gate_holds_up_no_other()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await GateAsync(project.Id, PipelineStage.Voice);
        _board.Answer(Recut("S02"));
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S03");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments.Where(s => s.Id is "S02" or "S03").All(s => s.State == StageState.NeedsReview));

        await client.ApproveSegmentAsync(project.Id, PipelineStage.Voice, "S02", 2);

        Assert.Null((await CutAsync(client, project.Id, "S02", 10)).Error);
        Assert.True((await client.GetVoiceAsync(project.Id)).Segments.Single(s => s.Id == "S03").Rerun);   // still waits for you
    }

    [Fact]
    public async Task A_rerun_from_your_own_picture_keeps_it_and_makes_the_still_starting_from_it()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var file = Path.Combine(_engine.DataDirectory, "mine.png");
        await File.WriteAllBytesAsync(file, BlankCanvas.Png(1920, 1080));
        await client.ReplaceStillAsync(project.Id, keys["S01·3"], file);
        var plan = await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);
        Assert.Equal(("This keeps your own picture of S01·3 and remakes the still of S01·4, which starts from it. Other stills stay as they are.", true), (plan.Text, plan.CanRun));

        await client.RerunFromHereAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);

        var stills = await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Single(s => s.Key == keys["S01·4"]).Version == 2);
        Assert.Equal(2, stills.Stills.Single(s => s.Key == keys["S01·3"]).Version);   // yours, untouched
        Assert.True(stills.Stills.Single(s => s.Key == keys["S01·3"]).Still!.YourFile);
    }

    [Fact]
    public async Task A_rerun_with_nothing_to_make_says_so_and_cannot_run()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var file = Path.Combine(_engine.DataDirectory, "mine.png");
        await File.WriteAllBytesAsync(file, BlankCanvas.Png(1920, 1080));
        await client.ReplaceStillAsync(project.Id, keys["S01·4"], file);

        var plan = await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·4"]);

        Assert.Equal(("Your own picture of S01·4 stays, and no still starts from it: nothing is made again.", false), (plan.Text, plan.CanRun));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RerunFromHereAsync(project.Id, PipelineStage.Stills, keys["S01·4"]));
    }

    [Fact]
    public async Task The_question_says_when_stills_waiting_in_the_batch_are_made_with_them()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await client.QueueStillAsync(project.Id, keys["S03·5"]);

        Assert.Equal(
            "This remakes the still of S01·2. Other stills stay as they are. The still waiting in the batch is made in the same run.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·2"])).Text);
    }

    [Fact]
    public async Task Retry_plans_the_whole_storyboard_when_the_segments_a_failed_cut_was_for_are_gone()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        await using (var db = await _engine.DbAsync())
        {
            var board = await db.Cells.SingleAsync(c => c.ProjectId == project.Id && c.Stage == PipelineStage.Storyboard && c.Key == "");
            board.State = StageState.Failed;
            board.RerunScope = "S09";   // cut for a segment a script written since no longer has
            await db.SaveChangesAsync();
        }
        _board.Answer(Board());
        var asked = _board.Requests.Count;

        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);

        var view = await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State is StageState.NeedsReview or StageState.Failed);
        Assert.Null(view.Error);
        Assert.Equal((asked + 1, 4), (_board.Requests.Count, _board.Requests[^1].Segments.Count));
    }

    [Fact]
    public async Task The_question_says_what_a_rerun_from_a_shot_or_a_still_makes_again()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        Assert.Equal(
            "This remakes shot S01·1, its still and the still of S01·2, which starts from it. Other shots stay as they are.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Storyboard, keys["S01·1"])).Text);
        Assert.Equal(
            "This remakes the still of S01·2. Other stills stay as they are.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·2"])).Text);
        Assert.Equal(
            "This remakes the still of S01·3 and the still of S01·4, which starts from it. Other stills stay as they are.",
            (await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·3"])).Text);
    }

    [Fact]
    public async Task A_rerun_from_a_still_makes_it_and_the_one_starting_from_it_in_one_run()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var runs = StillRuns().Count;

        await client.RerunFromHereAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);

        var stills = await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Count(s => s.Version == 2) == 2);
        Assert.Equal(new[] { keys["S01·3"], keys["S01·4"] }.Order(StringComparer.Ordinal), stills.Stills.Where(s => s.Version == 2).Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Equal(runs + 2, StillRuns().Count);
    }
}
