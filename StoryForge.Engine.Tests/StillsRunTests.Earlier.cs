using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.References;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Tests;

/// <summary>
/// #78: someone seen again goes in by the earlier still the shot writer picked, even with no
/// reference pictures; a picture they already have stays their source.
/// </summary>
public sealed partial class StillsRunTests
{
    private static ProjectSetup TextOnly(ProjectSetup setup) => setup with { References = setup.References with { Consistency = Consistency.TextToImageOnly } };

    private void TakesMany() => _stillInputs = [.. FakeImageComfy.Inputs, new("images", "#1.files_json"), new("negative", "#6.text")];

    /// <summary>
    /// <see cref="Board"/> with no continuations: S01·1 the soul coin, then Karlach. S01·3 picks Karlach
    /// from S01·2; S02·2 shows both, Karlach from S01·2 first and the coin from S01·1.
    /// </summary>
    private static StoryboardOutput Picking()
    {
        var board = Board(_ => false);
        var (s01, s02) = (board.Segments[0], board.Segments[1]);
        return board with
        {
            Segments =
            [
                s01 with { Shots = [s01.Shots[0], s01.Shots[1], s01.Shots[2] with { EarlierStills = [new("Karlach", "S01-2")] }, .. s01.Shots.Skip(3)] },
                s02 with { Shots = [s02.Shots[0], s02.Shots[1] with { Cast = ["Karlach", "Soul coin"], EarlierStills = [new("Karlach", "S01-2"), new("Soul coin", "S01-1")] }, .. s02.Shots.Skip(2)] },
                .. board.Segments.Skip(2),
            ],
        };
    }

    private JsonObject RunOf(string name) => StillRuns().Last(r => Prompt(r).Contains(name + ",", StringComparison.Ordinal));

    private static IReadOnlyList<string> Images(JsonObject workflow) =>
        [.. JsonNode.Parse((string)workflow["1"]!["inputs"]!["files_json"]!)!.AsArray().Select(f => (string)f!["name"]!)];

    private static string Uploaded(StillsView view, string key) => "uploaded-" + Path.GetFileName(view.Stills.Single(s => s.Key == key).Still!.File);

    [Fact]
    public async Task Seen_again_with_images_gets_one_earlier_still_per_cast_member()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        var keys = await KeysAsync(client, project.Id);
        var still = view.Stills.Single(s => s.Key == keys["S02·2"]).Still!;
        Assert.Equal([new EarlierStill("Karlach", keys["S01·2"]), new EarlierStill("Soul coin", keys["S01·1"])], still.EarlierStills);
        Assert.Empty(still.References);
        Assert.Equal([Uploaded(view, keys["S01·2"]), Uploaded(view, keys["S01·1"])], Images(RunOf("S02-2")));
    }

    [Fact]
    public async Task The_prompt_writer_hears_what_each_earlier_still_gives_and_gets_every_look()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());

        await StillsAsync(client, project.Id);

        var shot = _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2");
        Assert.Equal(
            [new StillPicture("Karlach", "The look of Karlach.", "S01-2"), new StillPicture("Soul coin", "The look of Soul coin.", "S01-1")],
            shot.Pictures);
        Assert.Empty(shot.Described);
        var first = _prompts.Requests.Single().Shots.Single(s => s.Label == "S01·1");
        Assert.Empty(first.Pictures);
        Assert.Equal([new StillPicture("Soul coin", "The look of Soul coin.")], first.Described);
    }

    [Fact]
    public async Task Seen_again_with_image_gets_the_first_pick_and_the_others_in_words()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Equal([new EarlierStill("Karlach", keys["S01·2"])], view.Stills.Single(s => s.Key == keys["S02·2"]).Still!.EarlierStills);
        Assert.Equal(Uploaded(view, keys["S01·2"]), Image(RunOf("S02-2")));
        var shot = _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2");
        Assert.Equal(["Karlach"], shot.Pictures.Select(p => p.Name));
        Assert.Equal([new StillPicture("Soul coin", "The look of Soul coin.")], shot.Described);
    }

    [Fact]
    public async Task With_a_reference_picture_the_reference_stays_the_source()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: Picking());

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var still = view.Stills.Single(s => s.Key == keys["S02·2"]).Still!;
        Assert.Null(still.EarlierStills);
        Assert.Equal(2, still.References.Count);
    }

    [Fact]
    public async Task A_cast_member_with_no_picture_gets_the_earlier_still()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: Picking(), beforeApproval: async p =>
        {
            var karlach = (await client.GetReferencesAsync(p.Id)).References.Single(r => r.Reference.Name == "Karlach").Images.Single();
            File.Delete(karlach.Path!);
        });

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var still = view.Stills.Single(s => s.Key == keys["S02·2"]).Still!;
        Assert.Equal([new EarlierStill("Karlach", keys["S01·2"])], still.EarlierStills);
        Assert.Single(still.References);   // the coin's reference stays its source
    }

    [Fact]
    public async Task An_empty_pick_gets_words_only()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Board(_ => false));

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Null(view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.EarlierStills);
        Assert.Equal(["uploaded-blank-1344x768.png"], Images(RunOf("S01-3")));
        Assert.Equal([new StillPicture("Karlach", "The look of Karlach.")], _prompts.Requests.Single().Shots.Single(s => s.Label == "S01·3").Described);
    }

    [Fact]
    public async Task A_still_sent_for_two_cast_members_goes_once()
    {
        TakesMany();
        var board = Picking();
        var (s01, s02) = (board.Segments[0], board.Segments[1]);
        board = board with
        {
            Segments =
            [
                s01 with { Shots = [s01.Shots[0], s01.Shots[1] with { Cast = ["Karlach", "Soul coin"], EarlierStills = [new("Soul coin", "")] }, .. s01.Shots.Skip(2)] },
                s02 with { Shots = [s02.Shots[0], s02.Shots[1] with { EarlierStills = [new("Karlach", "S01-2"), new("Soul coin", "S01-2")] }, .. s02.Shots.Skip(2)] },
                .. board.Segments.Skip(2),
            ],
        };
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, board);

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Equal(2, view.Stills.Single(s => s.Key == keys["S02·2"]).Still!.EarlierStills!.Count);
        Assert.Equal([Uploaded(view, keys["S01·2"])], Images(RunOf("S02-2")));
        Assert.Equal([new StillPicture("Karlach and Soul coin", "The look of Karlach. The look of Soul coin.", "S01-2")], _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2").Pictures);
    }

    [Fact]
    public async Task A_place_keeps_its_surroundings_from_an_earlier_still()
    {
        TakesMany();
        var board = Board(_ => false);
        var s01 = board.Segments[0];
        board = board with
        {
            Segments =
            [
                s01 with { Shots = [s01.Shots[0], s01.Shots[1] with { Cast = ["Mammon's mint"], EarlierStills = [] }, s01.Shots[2] with { Cast = ["Mammon's mint"], EarlierStills = [new("Mammon's mint", "S01-2")] }, .. s01.Shots.Skip(3)] },
                .. board.Segments.Skip(1),
            ],
        };
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, board);

        await StillsAsync(client, project.Id);

        Assert.True(_prompts.Requests.Single().Shots.Single(s => s.Label == "S01·3").Pictures.Single().KeepSurroundings);
    }

    [Fact]
    public async Task A_picked_still_whose_file_is_gone_goes_by_words()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());
        var view = await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        File.Delete(view.Stills.Single(s => s.Key == keys["S01·2"]).Path!);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);
        var after = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });

        Assert.Null(after.Error);
        Assert.Null(after.Stills.Single(s => s.Key == keys["S01·3"]).Still!.EarlierStills);
    }

    [Fact]
    public async Task Your_own_picture_can_be_picked()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var file = Path.Combine(_engine.DataDirectory, "mine.png");
        await File.WriteAllBytesAsync(file, BlankCanvas.Png(1344, 768));
        await client.ReplaceStillAsync(project.Id, keys["S01·2"], file);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);
        var after = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });

        Assert.Equal([new EarlierStill("Karlach", keys["S01·2"])], after.Stills.Single(s => s.Key == keys["S01·3"]).Still!.EarlierStills);
        Assert.Equal([Uploaded(after, keys["S01·2"])], Images(StillRuns()[^1]));
    }

    [Fact]
    public async Task A_still_remade_alone_before_its_pick_exists_goes_by_words()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        // A shot split off later has no still yet; the shot after it picks it.
        await client.SplitShotAsync(project.Id, keys["S01·2"], 15);
        var board = await client.GetStoryboardAsync(project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);
        _board.AnswerShot(new ShotRewriteOutput("Karlach by the fire.", ["Karlach"], false, [new("Karlach", "S01-3")]));
        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, keys["S01·3"]);
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.Shots.Single(s => s.Key == keys["S01·3"]).Version == 2);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·3"]);
        var after = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·3"]) is { Version: 2, State: not StageState.Running });

        Assert.Null(after.Stills.Single(s => s.Key == keys["S01·3"]).Still!.EarlierStills);
        Assert.Equal(["uploaded-blank-1344x768.png"], Images(StillRuns()[^1]));
        Assert.Equal(3, board.Shots.Single(s => s.Key == keys["S01·3"]).Number - 1);
    }

    [Fact]
    public async Task A_batch_paints_from_the_new_still_of_the_same_run()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());
        var before = await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await client.QueueStillAsync(project.Id, keys["S01·2"]);
        await client.QueueStillAsync(project.Id, keys["S01·3"]);

        await client.ContinuePipelineAsync(project.Id);
        var after = await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Single(s => s.Key == keys["S01·3"]).Version == 2);

        Assert.NotEqual(Uploaded(before, keys["S01·2"]), Uploaded(after, keys["S01·2"]));
        Assert.Equal([Uploaded(after, keys["S01·2"])], Images(RunOf("S01-3")));
    }

    [Fact]
    public async Task A_run_with_picks_shows_no_still_out_of_date()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());

        var view = await StillsAsync(client, project.Id);

        Assert.All(view.Stills, s => Assert.Null(s.Stale));
    }

    [Fact]
    public async Task A_profile_taking_no_picture_goes_by_words_without_error()
    {
        _stillInputs = [.. _stillInputs.Where(i => i.Key != "image")];
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());

        var view = await StillsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.All(view.Stills, s => Assert.Null(s.Still!.EarlierStills));
        Assert.Empty(_prompts.Requests.Single().Shots.Single(s => s.Label == "S01·3").Pictures);
    }
}
