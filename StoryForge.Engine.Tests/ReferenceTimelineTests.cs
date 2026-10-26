using StoryForge.Client;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The References timeline: every shot with where it starts and who and what is in it; putting a
/// reference in a shot or taking it out changes the shot's who and what, as the Board panel does.
/// </summary>
public sealed partial class ReferencesRunTests
{
    private static async Task<ShotView> ShotAsync(IStoryForgeClient client, Guid projectId, string key) =>
        (await client.GetStoryboardAsync(projectId)).Shots.Single(s => s.Key == key);

    private static IReadOnlyList<string> ShotsOf(ReferencesView view, string name) =>
        view.References.Single(r => r.Reference.Name == name).Reference.Shots;

    [Fact]
    public async Task The_timeline_has_every_shot_with_where_it_starts_in_the_video_and_who_is_in_it()
    {
        var (client, projectId) = await ListedAsync();

        var shots = (await client.GetReferencesAsync(projectId)).Shots;

        Assert.Equal(60, shots.Count);
        Assert.Equal(("S01·1", 0), (shots[0].Label, shots[0].Start));
        Assert.Equal(shots[0].Seconds, shots[1].Start, 3);
        Assert.Equal(["Soul coin"], shots[0].Cast);
        Assert.Equal("S02·1", shots[15].Label);
        Assert.Equal(75, shots[15].Start, 3);
        Assert.Equal(["Karlach"], shots[1].Cast);
    }

    [Fact]
    public async Task A_reference_put_in_a_shot_on_the_timeline_is_in_its_who_and_what_and_the_shot_stays_approved()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];

        await client.SetReferenceInShotAsync(projectId, "R02", shot.Key, true);

        Assert.Equal(["Karlach", "Soul coin"], (await ShotAsync(client, projectId, shot.Key)).Shot.Cast);
        Assert.Equal(StageState.Approved, (await ShotAsync(client, projectId, shot.Key)).State);
        Assert.Contains(shot.Key, ShotsOf(await client.GetReferencesAsync(projectId), "Soul coin"));
        Assert.Equal(StageState.Approved, (await client.GetProjectAsync(projectId)).Stages.Single(s => s.Stage == PipelineStage.Storyboard).State);
    }

    [Fact]
    public async Task A_reference_taken_out_of_a_shot_on_the_timeline_leaves_its_who_and_what()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];

        await client.SetReferenceInShotAsync(projectId, "R01", shot.Key, false);

        Assert.Empty((await ShotAsync(client, projectId, shot.Key)).Shot.Cast);
        Assert.DoesNotContain(shot.Key, ShotsOf(await client.GetReferencesAsync(projectId), "Karlach"));
    }

    [Fact]
    public async Task A_full_shot_takes_no_more()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];
        await client.EditShotAsync(projectId, shot.Key, new ShotChange(Cast: ["Karlach", "A tavern mug", "Mammon's mint", "Raphael"]));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.SetReferenceInShotAsync(projectId, "R02", shot.Key, true));

        Assert.Equal("S01·2 has 4 in it already: what Minimax takes per clip. Take one out first.", refused.Message);
        Assert.Equal(4, (await ShotAsync(client, projectId, shot.Key)).Shot.Cast.Count);
    }

    [Fact]
    public async Task A_change_in_the_Board_panel_shows_on_the_timeline()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];

        await client.EditShotAsync(projectId, shot.Key, new ShotChange(Cast: ["Soul coin", "Mammon's mint"]));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Contains(shot.Key, ShotsOf(view, "Soul coin"));
        Assert.DoesNotContain(shot.Key, ShotsOf(view, "Karlach"));
        Assert.Equal(["Soul coin", "Mammon's mint"], view.Shots[1].Cast);
        Assert.Equal(1, Assert.Single(view.Others).Shots);
    }

    [Fact]
    public async Task A_reference_merged_into_another_takes_its_shots_along_and_leaves_them_under_both_names()
    {
        var (client, projectId) = await ListedAsync();
        var coinShot = (await client.GetReferencesAsync(projectId)).Shots[0];

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));
        Assert.Contains(coinShot.Key, ShotsOf(await client.GetReferencesAsync(projectId), "Karlach"));

        await client.SetReferenceInShotAsync(projectId, "R01", coinShot.Key, false);

        Assert.Empty((await ShotAsync(client, projectId, coinShot.Key)).Shot.Cast);
        Assert.DoesNotContain(coinShot.Key, ShotsOf(await client.GetReferencesAsync(projectId), "Karlach"));
    }

    [Fact]
    public async Task Putting_a_reference_in_a_shot_it_is_in_already_changes_nothing()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];

        await client.SetReferenceInShotAsync(projectId, "R01", shot.Key, true);

        Assert.Equal(1, (await ShotAsync(client, projectId, shot.Key)).Version);
    }

    [Fact]
    public async Task A_shot_or_reference_that_is_not_there_is_refused()
    {
        var (client, projectId) = await ListedAsync();
        var shot = (await client.GetReferencesAsync(projectId)).Shots[1];

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.SetReferenceInShotAsync(projectId, "R09", shot.Key, true));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.SetReferenceInShotAsync(projectId, "R01", "S09/zzzz", true));
    }
}
