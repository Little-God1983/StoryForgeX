using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
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

    /// <summary>S01·3 is the mint; S02·2 shows Karlach in it, picked in that order.</summary>
    private static StoryboardOutput InTheMint()
    {
        var board = Board(_ => false);
        var (s01, s02) = (board.Segments[0], board.Segments[1]);
        return board with
        {
            Segments =
            [
                s01 with { Shots = [s01.Shots[0], s01.Shots[1], s01.Shots[2] with { Cast = ["Mammon's mint"], EarlierStills = [] }, .. s01.Shots.Skip(3)] },
                s02 with { Shots = [s02.Shots[0], s02.Shots[1] with { Cast = ["Karlach", "Mammon's mint"], EarlierStills = [new("Karlach", "S01-2"), new("Mammon's mint", "S01-3")] }, .. s02.Shots.Skip(2)] },
                .. board.Segments.Skip(2),
            ],
        };
    }

    [Fact]
    public async Task A_place_goes_in_first_whatever_order_it_was_picked_in()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, InTheMint());

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Equal([Uploaded(view, keys["S01·3"]), Uploaded(view, keys["S01·2"])], Images(RunOf("S02-2")));
        Assert.Equal(["Mammon's mint", "Karlach"], _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2").Pictures.Select(p => p.Name));
    }

    [Fact]
    public async Task A_picked_place_goes_in_before_a_reference_picture()
    {
        TakesMany();
        _pictures.ByName["Mammon's mint"] = [FakePictures.Picture("Mint.png", page: "https://bg3.wiki/wiki/Mint")];
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: InTheMint(), beforeApproval: async p =>
        {
            var mint = (await client.GetReferencesAsync(p.Id)).References.Single(r => r.Reference.Name == "Mammon's mint").Images.Single();
            File.Delete(mint.Path!);
        });

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Equal(2, Images(RunOf("S02-2")).Count);
        Assert.Equal(Uploaded(view, keys["S01·3"]), Images(RunOf("S02-2"))[0]);
        Assert.Equal(["Mammon's mint", "Karlach"], _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2").Pictures.Select(p => p.Name));
    }

    [Fact]
    public async Task A_place_with_its_own_reference_picture_goes_in_first()
    {
        TakesMany();
        _pictures.ByName["Mammon's mint"] = [FakePictures.Picture("Mint.png", page: "https://bg3.wiki/wiki/Mint")];
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: InTheMint());

        await StillsAsync(client, project.Id);

        Assert.Equal(["Mammon's mint", "Karlach"], _prompts.Requests.Single().Shots.Single(s => s.Label == "S02·2").Pictures.Select(p => p.Name));
    }

    [Fact]
    public async Task With_no_place_the_main_subject_goes_in_first()
    {
        TakesMany();
        var board = Picking();
        var s02 = board.Segments[1];
        board = board with
        {
            Segments =
            [
                board.Segments[0],
                s02 with { Shots = [s02.Shots[0], s02.Shots[1] with { EarlierStills = [new("Soul coin", "S01-1"), new("Karlach", "S01-2")] }, .. s02.Shots.Skip(2)] },
                .. board.Segments.Skip(2),
            ],
        };
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, board);

        var view = await StillsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        Assert.Equal([Uploaded(view, keys["S01·2"]), Uploaded(view, keys["S01·1"])], Images(RunOf("S02-2")));
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

    /// <summary><see cref="Picking"/>, with S01·4 painted from S01·3: a chain from S01·2.</summary>
    private static StoryboardOutput Chain()
    {
        var board = Picking();
        var s01 = board.Segments[0];
        return board with
        {
            Segments = [s01 with { Shots = [.. s01.Shots.Take(3), s01.Shots[3] with { EarlierStills = [new("Karlach", "S01-3")] }, .. s01.Shots.Skip(4)] }, .. board.Segments.Skip(1)],
        };
    }

    private static Still Made(string key, string? continues = null, string? earlier = null, bool yours = false, params string[] references) => new(
        key, key + ".png", "p", 1, new GenerationSize("16:9", 1344, 768), "w", references, continues, false, yours,
        earlier is null ? null : [new EarlierStill("Karlach", earlier)]);

    [Fact]
    public void Redo_walks_a_chain_of_earlier_stills()
    {
        var stills = new Dictionary<string, Still> { ["c"] = Made("c", earlier: "b"), ["a"] = Made("a"), ["b"] = Made("b", earlier: "a"), ["z"] = Made("z") };

        var redo = PipelineRunner.Redo(PipelineStage.Storyboard, new HashSet<string> { "a" }, stills, stills.Keys.ToHashSet());

        Assert.Equal(["a", "b", "c"], redo.Order());
    }

    [Fact]
    public void Your_own_picture_stops_a_chain()
    {
        var stills = new Dictionary<string, Still> { ["a"] = Made("a"), ["b"] = Made("b", earlier: "a", yours: true), ["c"] = Made("c", earlier: "b") };

        var redo = PipelineRunner.Redo(PipelineStage.Storyboard, new HashSet<string> { "a" }, stills, stills.Keys.ToHashSet());

        Assert.Equal(["a"], redo.Order());
    }

    [Fact]
    public void A_still_painted_from_a_shot_that_is_gone_is_not_remade_but_a_continuation_of_one_is()
    {
        var stills = new Dictionary<string, Still> { ["b"] = Made("b", earlier: "gone"), ["c"] = Made("c", continues: "gone") };

        var redo = PipelineRunner.Redo(PipelineStage.Storyboard, new HashSet<string> { "new" }, stills, new HashSet<string> { "b", "c", "new" });

        Assert.Equal(["c"], redo.Order());
    }

    [Fact]
    public void An_image_reaches_through_earlier_stills()
    {
        var stills = new Dictionary<string, Still> { ["a"] = Made("a", references: "R01/1"), ["b"] = Made("b", earlier: "a"), ["z"] = Made("z") };

        var redo = PipelineRunner.Redo(PipelineStage.References, new HashSet<string> { "R01/1" }, stills, stills.Keys.ToHashSet());

        Assert.Equal(["a", "b"], redo.Order());
    }

    [Fact]
    public async Task The_rerun_question_names_the_stills_painted_from_it()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Chain());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        var plan = await client.GetRerunPlanAsync(project.Id, PipelineStage.Stills, keys["S01·2"]);

        Assert.Contains("the still of S01·3, which is painted from it", plan.Text);
        Assert.Contains("the still of S01·4, which is painted from it", plan.Text);
        Assert.Contains("the still of S02·2, which is painted from it", plan.Text);
    }

    [Fact]
    public async Task Remaking_the_source_puts_the_stills_painted_from_it_out_of_date()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Chain());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01·2"]);
        var after = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01·2"]) is { Version: 2, State: not StageState.Running });

        string? Why(string label) => after.Stills.Single(s => s.Key == keys[label]).Stale;
        Assert.Equal("The still of S01·2 it was painted from was made again since.", Why("S01·3"));
        Assert.Equal("The still of S01·3 it was painted from is out of date.", Why("S01·4"));
        Assert.Null(Why("S01·5"));
    }

    [Fact]
    public async Task A_recut_puts_the_stills_painted_from_it_out_of_date()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, BoardPickingFromS02());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var runs = StillRuns().Count;
        _board.Answer(Recut("S02") with { LaterShots = [PickedAgain] });

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await CutAsync(client, project.Id, "S02", 10);

        var view = await client.GetStillsAsync(project.Id);
        Assert.NotNull(view.Stills.Single(s => s.Key == keys["S03·2"]).Stale);
        Assert.Equal(1, view.Stills.Single(s => s.Key == keys["S03·2"]).Version);
        Assert.Null(view.Stills.Single(s => s.Key == keys["S03·3"]).Stale);
        Assert.Equal(runs, StillRuns().Count);
    }

    [Fact]
    public async Task An_image_counts_the_stills_painted_from_those_it_went_into()
    {
        TakesMany();
        var board = Picking();
        var s01 = board.Segments[0];
        board = board with
        {
            Segments =
            [
                s01 with { Shots = [s01.Shots[0], s01.Shots[1] with { Cast = ["Karlach", "Soul coin"], EarlierStills = [new("Soul coin", "")] }, .. s01.Shots.Skip(2)] },
                .. board.Segments.Skip(1),
            ],
        };
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: board, beforeApproval: async p =>
        {
            var karlach = (await client.GetReferencesAsync(p.Id)).References.Single(r => r.Reference.Name == "Karlach").Images.Single();
            File.Delete(karlach.Path!);
        });
        var view = await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        Assert.Equal([new EarlierStill("Karlach", keys["S01·2"])], view.Stills.Single(s => s.Key == keys["S01·3"]).Still!.EarlierStills);

        var references = await client.GetReferencesAsync(project.Id);

        var coin = references.References.Single(r => r.Reference.Name == "Soul coin").Reference.Id;
        var direct = view.Stills.Count(s => s.Still!.References.Any(k => k.StartsWith(coin + "/", StringComparison.Ordinal)));
        Assert.True(references.StillsUsing[coin] > direct);
    }

    [Fact]
    public async Task A_recut_keeps_a_look_you_typed()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly);
        await StillsAsync(client, project.Id);
        await client.SetCastLookAsync(project.Id, "Karlach", "Horned, with one eye.");
        _board.Answer(Recut("S02"));

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        var board = await CutAsync(client, project.Id, "S02", 10);
        var karlach = board.Cast.Single(c => c.Name == "Karlach");
        Assert.Equal(("Horned, with one eye.", true), (karlach.Look, karlach.LookByYou));
    }

    [Fact]
    public async Task Editing_a_look_puts_stills_of_shots_with_them_out_of_date()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Board(_ => false));
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.SetCastLookAsync(project.Id, "Karlach", "Horned, with one eye.");

        var view = await client.GetStillsAsync(project.Id);
        Assert.NotNull(view.Stills.Single(s => s.Key == keys["S01\u00b72"]).Stale);
        Assert.Null(view.Stills.Single(s => s.Key == keys["S01\u00b71"]).Stale);
    }

    [Fact]
    public async Task Editing_the_look_of_someone_with_a_reference_is_refused()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetCastLookAsync(project.Id, "Karlach", "Horned."));

        Assert.Contains("References", refused.Message);
        var view = await client.GetStoryboardAsync(project.Id);
        Assert.Equal(view.ReferencedCast["Karlach"], (await client.GetReferencesAsync(project.Id)).References.Single(r => r.Reference.Name == "Karlach").Reference.Look);
    }

    [Fact]
    public async Task A_reference_starts_from_the_shot_writers_look_not_the_picture()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client);

        await StillsAsync(client, project.Id);

        Assert.Equal("The look of Karlach.", (await client.GetReferencesAsync(project.Id)).References.Single(r => r.Reference.Name == "Karlach").Reference.Look);
    }

    [Fact]
    public async Task A_cast_member_whose_pick_is_the_shot_before_keeps_their_look_in_the_prompt()
    {
        TakesMany();
        var board = Board(i => i == 2);
        var s01 = board.Segments[0];
        board = board with
        {
            Segments = [s01 with { Shots = [s01.Shots[0], s01.Shots[1], s01.Shots[2] with { EarlierStills = [new("Karlach", "S01-2")] }, .. s01.Shots.Skip(3)] }, .. board.Segments.Skip(1)],
        };
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, board);

        await StillsAsync(client, project.Id);

        var shot = _prompts.Requests.Single().Shots.Single(s => s.Label == "S01\u00b73");
        Assert.True(shot.FromShotBefore);
        Assert.Single(shot.Pictures);
        Assert.Equal([new StillPicture("Karlach", "The look of Karlach.")], shot.Described);
    }

    [Fact]
    public async Task A_pick_never_used_does_not_put_a_still_out_of_date_when_it_is_cut_again()
    {
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, board: BoardPickingFromS02());   // Karlach has a reference picture
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        Assert.Contains("Other segments stay as they are", (await client.GetRerunPlanAsync(project.Id, PipelineStage.Voice, "S02")).Text);
        _board.Answer(Recut("S02") with { LaterShots = [PickedAgain] });

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");
        await CutAsync(client, project.Id, "S02", 10);

        Assert.Null((await client.GetStillsAsync(project.Id)).Stills.Single(s => s.Key == keys["S03\u00b72"]).Stale);
    }

    [Fact]
    public async Task A_pick_of_a_shot_they_were_taken_out_of_goes_by_words()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, Picking());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.EditShotAsync(project.Id, keys["S01\u00b72"], new ShotChange(Cast: ["Soul coin"]));
        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Stills, keys["S01\u00b73"]);
        var after = await StillsAsync(client, project.Id, v => v.Stills.Single(s => s.Key == keys["S01\u00b73"]) is { Version: 2, State: not StageState.Running });

        Assert.Null(after.Stills.Single(s => s.Key == keys["S01\u00b73"]).Still!.EarlierStills);
        Assert.Equal(["Karlach"], _prompts.Requests[^1].Shots.Single().Described.Select(p => p.Name));
    }

    [Fact]
    public async Task A_shot_you_edited_stays_yours_when_a_recut_makes_it_pick_again()
    {
        TakesMany();
        var client = await StartAsync();
        var project = await ReferencesApprovedAsync(client, TextOnly, BoardPickingFromS02());
        await StillsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await client.EditShotAsync(project.Id, keys["S03\u00b72"], new ShotChange(Description: "What you want."));
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);
        _board.Answer(Recut("S02") with { LaterShots = [PickedAgain] });

        await client.RerunFromHereAsync(project.Id, PipelineStage.Voice, "S02");

        var board = await CutAsync(client, project.Id, "S02", 10);
        var later = board.Shots.Single(s => s.Key == keys["S03\u00b72"]);
        Assert.Equal((3, VersionOrigin.Edited), (later.Version, later.Versions[^1].Origin));
    }
}
