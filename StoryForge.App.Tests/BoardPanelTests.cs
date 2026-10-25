using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.Script;
using StoryForge.Client;
using static StoryForge.App.Tests.StoryboardMatrixTests;

namespace StoryForge.App.Tests;

/// <summary>
/// The Board panel of 2b (what board 8 drew): what a shot shows, the words it covers, who is in
/// it, and what you change on it: the description, who is in it, "continues", its cuts, split and merge.
/// </summary>
public sealed class BoardPanelTests
{
    private readonly FakeStoryForgeClient _client = new();

    private async Task<ScriptMatrixViewModel> OpenAsync(params ShotView[] shots)
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook"), Segment("S02", "Outro")], "", 20, 60, []);
        _client.Storyboards[project.Id] = Planned(project.Id, StageState.NeedsReview, shots);
        var matrix = new ResultMatrixPageViewModel(_client, _ => { });
        matrix.Show(project);
        await matrix.ScriptLoading;
        return matrix.Script!;
    }

    private static ShotRowViewModel Pick(ScriptMatrixViewModel script, int row, int shot)
    {
        var picked = script.Rows[row].Shots[shot];
        picked.SelectCommand.Execute(null);
        return picked;
    }

    [Fact]
    public async Task The_panel_shows_what_the_shot_shows_and_lights_the_words_it_covers()
    {
        var script = await OpenAsync();

        Pick(script, 0, 0);

        Assert.Equal("A coin on a stone table.", script.BoardShows);
        Assert.Equal(10, script.BoardWords.Count);
        Assert.Equal([true, true, true, true, false, false, false, false, false, false], script.BoardWords.Select(w => w.IsInShot));
        Assert.Equal(("w0", "w3"), (script.BoardStartWord, script.BoardEndWord));
    }

    [Fact]
    public async Task Ending_a_shot_one_word_later_moves_its_cut()
    {
        var script = await OpenAsync();
        var shot = Pick(script, 0, 0);

        await script.EndLaterCommand.ExecuteAsync(null);
        Pick(script, 0, 1);
        await script.StartEarlierCommand.ExecuteAsync(null);

        Assert.Equal([("move End +1", shot.Key), ("move Start -1", "S01/0002")], _client.ShotActions);
    }

    [Fact]
    public async Task A_cut_that_would_leave_the_next_shot_under_two_seconds_cannot_be_moved()
    {
        // S01·2 is words 8 and 9: 2.2 s. Giving it one word less leaves 1.1 s.
        var script = await OpenAsync(Shot("S01", 1, 0, 8.125, 0, 8), Shot("S01", 2, 8.125, 10.3, 8, 2), Shot("S02", 1, 0, 5, 0, 10));

        Pick(script, 0, 0);

        Assert.False(script.EndLaterCommand.CanExecute(null));
        Assert.True(script.EndEarlierCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_start_of_a_segments_first_shot_and_the_end_of_its_last_cannot_move()
    {
        var script = await OpenAsync();

        Pick(script, 0, 0);
        Assert.False(script.StartEarlierCommand.CanExecute(null));
        Assert.False(script.StartLaterCommand.CanExecute(null));
        Assert.True(script.EndLaterCommand.CanExecute(null));

        Pick(script, 0, 1);
        Assert.False(script.EndLaterCommand.CanExecute(null));
        Assert.False(script.EndEarlierCommand.CanExecute(null));
        Assert.True(script.StartEarlierCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_description_saves_your_words_as_the_next_version()
    {
        var script = await OpenAsync();
        var shot = Pick(script, 0, 1);

        script.StartEditShotCommand.Execute(null);
        Assert.Equal("A coin on a stone table.", script.EditShotDescription);
        script.EditShotDescription = "  Karlach turns the coin in her fingers.  ";
        await script.SaveShotDescriptionCommand.ExecuteAsync(null);

        var (key, change) = _client.ShotChanges.Single();
        Assert.Equal((shot.Key, "Karlach turns the coin in her fingers.", null, null), (key, change.Description, change.Cast, change.ContinuesPrevious));
        Assert.False(script.IsEditingShot);
    }

    [Fact]
    public async Task Who_is_in_a_shot_comes_from_the_cast_and_a_full_shot_takes_no_more()
    {
        var script = await OpenAsync(Shot("S01", 1, 0, 4.2, 0, 4, cast: ["Karlach"]), Shot("S01", 2, 4.2, 10.3, 4, 6, cast: ["A", "B", "C", "D"]), Shot("S02", 1, 0, 5, 0, 10));

        Pick(script, 0, 0);
        Assert.Equal(["Karlach"], script.BoardCast);
        Assert.Equal(["Soul coin", "Raphael"], script.BoardCastChoices);
        await script.AddCastCommand.ExecuteAsync("Soul coin");
        await script.RemoveCastCommand.ExecuteAsync("Karlach");

        Assert.Equal([["Karlach", "Soul coin"], []], _client.ShotChanges.Select(c => c.Change.Cast!));
        Pick(script, 0, 1);
        Assert.False(script.AddCastCommand.CanExecute("Raphael"));
    }

    [Fact]
    public async Task Continues_the_shot_before_saves_when_ticked()
    {
        var script = await OpenAsync();
        var shot = Pick(script, 0, 1);
        Assert.False(script.BoardContinues);

        script.BoardContinues = true;
        await script.Saving;

        Assert.Equal((shot.Key, (bool?)true), (_client.ShotChanges.Single().ShotKey, _client.ShotChanges.Single().Change.ContinuesPrevious));
    }

    [Fact]
    public async Task A_shot_splits_at_a_word_inside_it()
    {
        var script = await OpenAsync();
        var shot = Pick(script, 0, 1);   // words 4 to 9

        Assert.False(script.SplitAtCommand.CanExecute(4));   // its first word: nothing before it
        Assert.False(script.SplitAtCommand.CanExecute(2));   // not in this shot
        Assert.True(script.BoardWords[6].CanSplit);
        await script.SplitAtCommand.ExecuteAsync(6);

        Assert.Equal(("split at 6", shot.Key), _client.ShotActions.Single());
    }

    [Fact]
    public async Task A_shot_merges_with_the_next_but_the_last_has_none()
    {
        var script = await OpenAsync();

        Pick(script, 0, 1);
        Assert.False(script.MergeWithNextCommand.CanExecute(null));
        var first = Pick(script, 0, 0);
        await script.MergeWithNextCommand.ExecuteAsync(null);

        Assert.Equal(("merge", first.Key), _client.ShotActions.Single());
    }

    [Fact]
    public async Task Approve_and_regenerate_act_on_the_selected_shot()
    {
        var script = await OpenAsync();
        var shot = Pick(script, 0, 0);

        await script.ApproveSegmentCommand.ExecuteAsync(null);
        await script.RegenerateSegmentCommand.ExecuteAsync(null);

        Assert.Equal([("approve v1", shot.Key), ("regenerate", shot.Key)], _client.ShotActions);
    }

    [Fact]
    public async Task A_refused_change_says_why()
    {
        var script = await OpenAsync();
        Pick(script, 0, 0);
        _client.StageFailure = new ArgumentException("S01·2 would last 1.5 s; a shot is at least 2 s.");

        await script.EndLaterCommand.ExecuteAsync(null);

        Assert.Equal("Could not move the cut: S01·2 would last 1.5 s; a shot is at least 2 s.", script.ActionError);
    }
}
