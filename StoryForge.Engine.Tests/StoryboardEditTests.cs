using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The storyboard as the matrix shows it, and what you change on a shot: its description, who is
/// in it, where it cuts, split and merge. Every change is a new version to approve, and a
/// segment's shots always add up to its audio.
/// </summary>
public sealed class StoryboardEditTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly ScriptRunTests.FakeScriptAgent _script = new();
    private readonly StoryboardRunTests.FakeStoryboardAgent _board = new();
    private readonly StoryboardRunTests.FakeFactCheck _facts = new();

    public void Dispose() => _engine.Dispose();

    /// <summary>The words of every segment: 150, one every 0.5 s (see <see cref="StoryboardRunTests.WordsVoice"/>).</summary>
    private static double CutAt(int word) => word == 0 ? 0 : word * 0.5 - 0.05;

    /// <summary><see cref="StoryboardRunTests.Good"/>, with S01 cut at <paramref name="firstWords"/> instead.</summary>
    private static StoryboardOutput Board(params int[] firstWords)
    {
        var good = StoryboardRunTests.Good();
        return (good with
        {
            Segments = [new("S01", [.. firstWords.Select((w, i) => new ShotOutput(w, $"S01 shot {i + 1}", ["Karlach"], false, []))]), .. good.Segments.Skip(1)],
        }).WithPicks();
    }

    /// <summary><see cref="StoryboardRunTests.Good"/>, with S01·4 painted from the Karlach of S01·3.</summary>
    private static StoryboardOutput Picking()
    {
        var good = StoryboardRunTests.Good();
        var first = good.Segments[0];
        return good with
        {
            Segments = [first with { Shots = [.. first.Shots.Take(3), first.Shots[3] with { EarlierStills = [new("Karlach", "S01-3")] }, .. first.Shots.Skip(4)] }, .. good.Segments.Skip(1)],
        };
    }

    [Fact]
    public async Task A_merge_moves_a_pick_to_the_surviving_shot()
    {
        var (client, projectId, view) = await PlannedAsync(Picking());
        var picker = view.Shots.Single(s => s.Label == "S01·4").Key;
        var kept = view.Shots.Single(s => s.Label == "S01·2").Key;

        await client.MergeShotAsync(projectId, kept);

        var shot = (await client.GetStoryboardAsync(projectId)).Shots.Single(s => s.Key == picker).Shot;
        Assert.Equal([new EarlierStill("Karlach", kept)], shot.EarlierStills);
    }

    [Fact]
    public async Task A_merge_drops_a_pick_when_the_surviving_shot_does_not_show_them()
    {
        var (client, projectId, view) = await PlannedAsync(Picking());
        var picker = view.Shots.Single(s => s.Label == "S01·4").Key;
        var kept = view.Shots.Single(s => s.Label == "S01·2").Key;
        await client.EditShotAsync(projectId, kept, new ShotChange(Cast: ["Soul coin"]));

        await client.MergeShotAsync(projectId, kept);

        Assert.Empty((await client.GetStoryboardAsync(projectId)).Shots.Single(s => s.Key == picker).Shot.EarlierStills!);
    }

    [Fact]
    public async Task A_split_gives_the_second_half_the_picks_of_the_shot()
    {
        var (client, projectId, view) = await PlannedAsync(Picking());
        var picker = view.Shots.Single(s => s.Label == "S01·4");

        await client.SplitShotAsync(projectId, picker.Key, picker.Shot.FirstWord + 5);

        var after = await client.GetStoryboardAsync(projectId);
        var second = after.Shots.Single(s => s.Label == "S01·5");
        Assert.Equal(picker.Shot.EarlierStills, second.Shot.EarlierStills);
    }

    [Fact]
    public async Task Taking_someone_out_of_a_shot_drops_their_pick()
    {
        var (client, projectId, view) = await PlannedAsync(Picking());
        var picker = view.Shots.Single(s => s.Label == "S01·4").Key;

        await client.EditShotAsync(projectId, picker, new ShotChange(Cast: ["Soul coin"]));

        Assert.Empty((await client.GetStoryboardAsync(projectId)).Shots.Single(s => s.Key == picker).Shot.EarlierStills!);
    }

    [Fact]
    public async Task Adding_someone_gives_no_pick()
    {
        var (client, projectId, view) = await PlannedAsync(Picking());
        var picker = view.Shots.Single(s => s.Label == "S01·4").Key;

        await client.EditShotAsync(projectId, picker, new ShotChange(Cast: ["Karlach", "Soul coin"]));

        Assert.Equal([new EarlierStill("Karlach", view.Shots.Single(s => s.Label == "S01·3").Key)], (await client.GetStoryboardAsync(projectId)).Shots.Single(s => s.Key == picker).Shot.EarlierStills);
    }

    [Fact]
    public async Task Editing_a_look_makes_a_new_sheet_version_and_marks_it_yours()
    {
        var (client, projectId, _) = await PlannedAsync();

        await client.SetCastLookAsync(projectId, " karlach ", "  Horned, with one eye.  ");

        var view = await client.GetStoryboardAsync(projectId);
        var karlach = view.Cast.Single(c => c.Name == "Karlach");
        Assert.Equal(("Horned, with one eye.", true), (karlach.Look, karlach.LookByYou));
        Assert.Equal(StageState.NeedsReview, view.State);
        Assert.False(view.Cast.Single(c => c.Name == "Soul coin").LookByYou);
    }

    [Fact]
    public async Task An_empty_look_is_refused()
    {
        var (client, projectId, _) = await PlannedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetCastLookAsync(projectId, "Karlach", "  "));
    }

    [Fact]
    public async Task Someone_not_on_the_cast_list_has_no_look_to_edit()
    {
        var (client, projectId, _) = await PlannedAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.SetCastLookAsync(projectId, "Raphael", "Red."));
    }

    [Fact]
    public async Task The_same_look_changes_nothing()
    {
        var (client, projectId, _) = await PlannedAsync();

        await client.SetCastLookAsync(projectId, "Karlach", "The look of Karlach.");

        Assert.False((await client.GetStoryboardAsync(projectId)).Cast.Single(c => c.Name == "Karlach").LookByYou);
    }

    [Fact]
    public async Task Editing_a_look_clears_the_fact_note_about_it()
    {
        var problem = new FactProblemOutput("Karlach", "F01", "F01 says otherwise.");
        _board.Answer(StoryboardRunTests.Good()).Answer(StoryboardRunTests.Good());
        _facts.Answer(problem).Answer(problem).Answer(problem);
        var (client, projectId, view) = await PlannedAsync(StoryboardRunTests.Good());
        Assert.Single(view.FactDisagreements!);

        await client.SetCastLookAsync(projectId, "Karlach", "What you want.");

        Assert.Empty((await client.GetStoryboardAsync(projectId)).FactDisagreements!);
    }

    [Fact]
    public async Task Editing_what_a_shot_shows_clears_its_fact_note()
    {
        var problem = new FactProblemOutput("S01-1", "F01", "F01 says otherwise.");
        _board.Answer(StoryboardRunTests.Good()).Answer(StoryboardRunTests.Good());
        _facts.Answer(problem).Answer(problem).Answer(problem);
        var (client, projectId, view) = await PlannedAsync(StoryboardRunTests.Good());
        var key = view.Shots[0].Key;
        Assert.NotNull(view.Shots[0].Shot.FactDisagreements);

        await client.EditShotAsync(projectId, key, new ShotChange(Description: "What you want."));

        Assert.Null((await client.GetStoryboardAsync(projectId)).Shots[0].Shot.FactDisagreements);
    }

    private async Task<(IStoryForgeClient Client, Guid ProjectId, StoryboardView View)> PlannedAsync(StoryboardOutput? board = null)
    {
        _board.Answer(board ?? StoryboardRunTests.Good());
        var client = await _engine.StartClientAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new ScriptRunTests.ThreeFacts()));
            services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(_script));
            services.Replace(ServiceDescriptor.Singleton<IStoryboardAgent>(_board));
            services.Replace(ServiceDescriptor.Singleton<IStoryboardFactCheck>(_facts));
            StoryboardRunTests.UseVoice<StoryboardRunTests.WordsVoice>(services);
        });
        _script.Answer(ScriptRunTests.Good());
        var project = await ScriptRunTests.ApprovedFactsAsync(client, s => s with { Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Script or PipelineStage.Voice))] });
        var view = await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State is StageState.NeedsReview or StageState.Failed);
        Assert.True(view.Error is null, view.Error);
        return (client, project.Id, view);
    }

    private static ShotView S01(StoryboardView view, int number) => view.Shots.Single(s => s.SegmentId == "S01" && s.Number == number);

    [Fact]
    public async Task The_storyboard_numbers_its_shots_by_where_they_start()
    {
        var (_, _, view) = await PlannedAsync();

        Assert.Equal(60, view.Shots.Count);
        Assert.Equal([.. Enumerable.Range(1, 15)], view.Shots.Where(s => s.SegmentId == "S01").Select(s => s.Number));
        Assert.Equal(["S01·1", "S01·2"], view.Shots.Take(2).Select(s => s.Label));
        Assert.Equal([0, 10], view.Shots.Take(2).Select(s => s.Shot.FirstWord));
        Assert.Equal("S02·1", view.Shots[15].Label);
        Assert.Equal((StageState.NeedsReview, 1, (int?)null), (view.Shots[0].State, view.Shots[0].Version, view.Shots[0].ApprovedVersion));
        Assert.Equal(150, view.Words["S01"].Count);
        Assert.Equal(["Karlach", "Soul coin", "Mammon's mint"], view.Cast.Select(c => c.Name));
        Assert.Equal((Pacing.Default, 20), (view.Pacing, view.MaxClipSeconds));
    }

    [Fact]
    public async Task The_storyboard_keeps_the_words_of_the_voice_it_was_cut_from()
    {
        var (client, projectId, _) = await PlannedAsync();

        // Spoken again slower: its words fall elsewhere now.
        await client.RegenerateSegmentAsync(projectId, PipelineStage.Voice, "S01");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(projectId), v => v.Segments.Single(s => s.Id == "S01").Version == 2);

        var view = await client.GetStoryboardAsync(projectId);
        Assert.Equal(0.5, view.Words["S01"][1].Start, 3);
        Assert.Equal(75, view.Shots.Where(s => s.SegmentId == "S01").Sum(s => s.Shot.Seconds), 3);
    }

    [Fact]
    public async Task A_change_to_a_shot_is_its_next_version_to_review()
    {
        var (client, projectId, view) = await PlannedAsync();
        var shot = S01(view, 2);

        await client.EditShotAsync(projectId, shot.Key, new ShotChange("Karlach turns the coin.", [" karlach", "Soul coin"], true));

        var changed = S01(await client.GetStoryboardAsync(projectId), 2);
        Assert.Equal((2, StageState.NeedsReview, VersionOrigin.Edited, (int?)1), (changed.Version, changed.State, changed.Versions[^1].Origin, changed.Versions[^1].BasedOn));
        Assert.Equal(("Karlach turns the coin.", true), (changed.Shot.Description, changed.Shot.ContinuesPrevious));
        Assert.Equal(["Karlach", "Soul coin"], changed.Shot.Cast);
        Assert.Equal((shot.Shot.Start, shot.Shot.End), (changed.Shot.Start, changed.Shot.End));
    }

    [Fact]
    public async Task A_change_with_more_than_four_in_the_shot_or_no_description_is_refused()
    {
        var (client, projectId, view) = await PlannedAsync();
        var key = S01(view, 2).Key;

        var crowded = await Assert.ThrowsAsync<ArgumentException>(() => client.EditShotAsync(projectId, key, new ShotChange(Cast: ["A", "B", "C", "D", "E"])));
        Assert.Equal("A shot takes at most 4: what Minimax takes per clip.", crowded.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EditShotAsync(projectId, key, new ShotChange(Description: "  ")));
        Assert.Equal(1, S01(await client.GetStoryboardAsync(projectId), 2).Version);
    }

    [Fact]
    public async Task Ending_a_shot_one_word_later_grows_it_and_shrinks_the_next()
    {
        var (client, projectId, view) = await PlannedAsync();

        await client.MoveCutAsync(projectId, S01(view, 1).Key, ShotEdge.End, 1);

        var after = await client.GetStoryboardAsync(projectId);
        var (first, second) = (S01(after, 1), S01(after, 2));
        Assert.Equal((0, 11, 2), (first.Shot.FirstWord, first.Shot.WordCount, first.Version));
        Assert.Equal((11, 9, 2), (second.Shot.FirstWord, second.Shot.WordCount, second.Version));
        Assert.Equal(CutAt(11), first.Shot.End, 3);
        Assert.Equal(CutAt(11), second.Shot.Start, 3);
        Assert.Equal(75, after.Shots.Where(s => s.SegmentId == "S01").Sum(s => s.Shot.Seconds), 3);
        Assert.All(after.Shots.Skip(2), s => Assert.Equal(1, s.Version));
    }

    [Fact]
    public async Task Starting_a_shot_one_word_earlier_takes_the_word_from_the_shot_before()
    {
        var (client, projectId, view) = await PlannedAsync();

        await client.MoveCutAsync(projectId, S01(view, 2).Key, ShotEdge.Start, -1);

        var after = await client.GetStoryboardAsync(projectId);
        Assert.Equal((9, 9), (S01(after, 1).Shot.WordCount, S01(after, 2).Shot.FirstWord));
    }

    [Fact]
    public async Task A_cut_that_would_leave_a_shot_under_two_seconds_is_refused()
    {
        // S01·2 is words 5 to 9: 2.5 s. One word less is 2 s, two words less 1.5 s.
        var (client, projectId, view) = await PlannedAsync(Board(0, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140));
        var key = S01(view, 2).Key;
        await client.MoveCutAsync(projectId, key, ShotEdge.End, -1);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.MoveCutAsync(projectId, key, ShotEdge.End, -1));

        Assert.Equal("S01·2 would last 1.5 s; a shot is at least 2 s.", refused.Message);
        Assert.Equal(4, S01(await client.GetStoryboardAsync(projectId), 2).Shot.WordCount);
    }

    [Fact]
    public async Task A_cut_that_would_leave_a_shot_longer_than_a_clip_is_refused()
    {
        // S01·1 is words 0 to 39: 19.95 s; one more word is over 20 s.
        var (client, projectId, view) = await PlannedAsync(Board(0, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.MoveCutAsync(projectId, S01(view, 1).Key, ShotEdge.End, 1));

        Assert.Equal("S01·1 would last 20.45 s; the video model makes at most 20 s.", refused.Message);
    }

    [Fact]
    public async Task The_start_of_a_segments_first_shot_and_the_end_of_its_last_do_not_move()
    {
        var (client, projectId, view) = await PlannedAsync();

        var start = await Assert.ThrowsAsync<ArgumentException>(() => client.MoveCutAsync(projectId, S01(view, 1).Key, ShotEdge.Start, 1));
        var end = await Assert.ThrowsAsync<ArgumentException>(() => client.MoveCutAsync(projectId, S01(view, 15).Key, ShotEdge.End, -1));

        Assert.Equal("S01·1 starts with its segment; that cut does not move.", start.Message);
        Assert.Equal("S01·15 ends with its segment; that cut does not move.", end.Message);
    }

    [Fact]
    public async Task A_cut_does_not_move_past_the_whole_shot_next_to_it()
    {
        var (client, projectId, view) = await PlannedAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.MoveCutAsync(projectId, S01(view, 1).Key, ShotEdge.End, 10));

        Assert.Equal("S01·2 would have no words left.", refused.Message);
    }

    [Fact]
    public async Task Splitting_a_shot_makes_two_and_the_shots_after_it_move_up_a_number()
    {
        var (client, projectId, view) = await PlannedAsync();
        var shot = S01(view, 1);

        await client.SplitShotAsync(projectId, shot.Key, 5);

        var after = await client.GetStoryboardAsync(projectId);
        Assert.Equal(16, after.Shots.Count(s => s.SegmentId == "S01"));
        Assert.Equal((shot.Key, 5, 2), (S01(after, 1).Key, S01(after, 1).Shot.WordCount, S01(after, 1).Version));
        var added = S01(after, 2);
        Assert.Equal((5, 5, 1, StageState.NeedsReview), (added.Shot.FirstWord, added.Shot.WordCount, added.Version, added.State));
        Assert.Equal((shot.Shot.Description, true), (added.Shot.Description, added.Shot.ContinuesPrevious));
        Assert.Equal(S01(view, 2).Key, S01(after, 3).Key);
        Assert.Equal(75, after.Shots.Where(s => s.SegmentId == "S01").Sum(s => s.Shot.Seconds), 3);
    }

    [Theory]
    [InlineData(2, "S01·1 would last 0.95 s; a shot is at least 2 s.")]
    [InlineData(10, "Word 10 is not inside S01·1, which is words 0 to 9.")]
    [InlineData(0, "Word 0 is not inside S01·1, which is words 0 to 9.")]
    public async Task A_split_that_breaks_a_limit_or_is_outside_the_shot_is_refused(int atWord, string reason)
    {
        var (client, projectId, view) = await PlannedAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.SplitShotAsync(projectId, S01(view, 1).Key, atWord));

        Assert.Equal(reason, refused.Message);
        Assert.Equal(15, (await client.GetStoryboardAsync(projectId)).Shots.Count(s => s.SegmentId == "S01"));
    }

    [Fact]
    public async Task Merging_a_shot_with_the_next_makes_one_shot_of_both()
    {
        var (client, projectId, view) = await PlannedAsync();
        var (first, second) = (S01(view, 1), S01(view, 2));

        await client.MergeShotAsync(projectId, first.Key);

        var after = await client.GetStoryboardAsync(projectId);
        Assert.Equal(14, after.Shots.Count(s => s.SegmentId == "S01"));
        var merged = S01(after, 1);
        Assert.Equal((first.Key, 20, 2), (merged.Key, merged.Shot.WordCount, merged.Version));
        Assert.Equal(second.Shot.End, merged.Shot.End, 3);
        Assert.DoesNotContain(after.Shots, s => s.Key == second.Key);
    }

    [Fact]
    public async Task A_merge_longer_than_a_clip_or_of_a_segments_last_shot_is_refused()
    {
        // 12.5 s shots: two of them are about 25 s, the model makes 20 s.
        var (client, projectId, view) = await PlannedAsync(Board(0, 25, 50, 75, 100, 125));

        var tooLong = await Assert.ThrowsAsync<ArgumentException>(() => client.MergeShotAsync(projectId, S01(view, 1).Key));
        var last = await Assert.ThrowsAsync<ArgumentException>(() => client.MergeShotAsync(projectId, S01(view, 6).Key));

        Assert.Equal("S01·1 would last 24.95 s; the video model makes at most 20 s.", tooLong.Message);
        Assert.Equal("S01·6 is the last shot of S01; there is no shot after it to merge with.", last.Message);
    }

    [Fact]
    public async Task Nothing_on_the_storyboard_changes_while_a_shot_is_planned_again()
    {
        var (client, projectId, view) = await PlannedAsync();
        _board.HoldShot();
        await client.RegenerateSegmentAsync(projectId, PipelineStage.Storyboard, S01(view, 1).Key);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.MoveCutAsync(projectId, S01(view, 3).Key, ShotEdge.End, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EditShotAsync(projectId, S01(view, 3).Key, new ShotChange("New.")));

        await client.CancelSegmentAsync(projectId, PipelineStage.Storyboard, S01(view, 1).Key);
    }

    [Fact]
    public async Task Approving_the_shots_after_an_edit_approves_the_storyboard()
    {
        var (client, projectId, view) = await PlannedAsync();
        await client.SplitShotAsync(projectId, S01(view, 1).Key, 5);

        await client.ApproveSegmentsAsync(projectId, PipelineStage.Storyboard);

        var after = await client.GetStoryboardAsync(projectId);
        Assert.Equal(StageState.Approved, after.State);
        Assert.All(after.Shots, s => Assert.Equal(StageState.Approved, s.State));
    }

    [Fact]
    public async Task A_project_whose_storyboard_has_not_started_shows_its_pacing()
    {
        var client = await _engine.StartClientAsync();
        var project = await client.CreateProjectAsync(await ProjectTests.ValidSetup(client) with { Pacing = new Pacing(2, 4, 6) });

        var view = await client.GetStoryboardAsync(project.Id);

        Assert.Equal((StageState.NotStarted, new Pacing(2, 4, 6), 20), (view.State, view.Pacing, view.MaxClipSeconds));
        Assert.Empty(view.Shots);
    }

    [Fact]
    public async Task An_older_version_that_cuts_elsewhere_cannot_be_picked_or_approved()
    {
        var (client, projectId, view) = await PlannedAsync();
        var key = S01(view, 1).Key;
        await client.MoveCutAsync(projectId, key, ShotEdge.End, 1);

        var select = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SelectSegmentVersionAsync(projectId, PipelineStage.Storyboard, key, 1));
        var approve = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentAsync(projectId, PipelineStage.Storyboard, key, 1));

        Assert.Equal("v1 of S01·1 cuts elsewhere: move the cut back instead, so the shots still add up to the audio.", select.Message);
        Assert.Equal(select.Message, approve.Message);
        Assert.Equal(2, S01(await client.GetStoryboardAsync(projectId), 1).Version);
    }

    [Fact]
    public async Task An_older_version_with_the_same_cuts_can_be_picked()
    {
        var (client, projectId, view) = await PlannedAsync();
        var key = S01(view, 1).Key;
        await client.EditShotAsync(projectId, key, new ShotChange("A new description."));

        await client.SelectSegmentVersionAsync(projectId, PipelineStage.Storyboard, key, 1);

        var shot = S01(await client.GetStoryboardAsync(projectId), 1);
        Assert.Equal((1, "S01 shot 1"), (shot.Version, shot.Shot.Description));
    }

    [Fact]
    public async Task The_view_names_the_versions_that_cut_elsewhere()
    {
        var (client, projectId, view) = await PlannedAsync();
        var key = S01(view, 1).Key;
        await client.EditShotAsync(projectId, key, new ShotChange("A new description."));
        await client.MoveCutAsync(projectId, key, ShotEdge.End, 1);

        var shot = S01(await client.GetStoryboardAsync(projectId), 1);

        Assert.Equal(3, shot.Version);
        Assert.Equal([1, 2], shot.MovedVersions);
    }
}
