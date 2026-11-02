using StoryForge.Client;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Storyboard;

namespace StoryForge.Engine.Tests;

/// <summary>How the engine times shots from the word timings, and checks the model's storyboard.</summary>
public sealed class StoryboardCheckTests
{
    /// <summary>
    /// <paramref name="count"/> words, one every 0.5 s, each heard for 0.4 s: word i from i·0.5 to
    /// i·0.5 + 0.4, so the pause before word i ends at i·0.5 and the cut there falls at i·0.5 − 0.05.
    /// </summary>
    private static SegmentWords Segment(string id, int count) =>
        new(id, [.. Enumerable.Range(0, count).Select(i => new SpokenWord($"w{i}", i * 0.5, i * 0.5 + 0.4))], count * 0.5);

    private static readonly IReadOnlyList<CastOutput> Cast =
    [
        new("Karlach", "character", "", "The look of Karlach."),
        new("Soul coin", "object", "", "The look of Soul coin."),
        new("Mammon's mint", "place", "Where every coin is made; the video turns on it.", "The look of Mammon's mint."),
    ];

    private static ShotOutput Shot(int firstWord, params string[] cast) => new(firstWord, "A coin on a stone table.", cast, false, []);

    private static IReadOnlyList<string> Problems(StoryboardOutput output, params SegmentWords[] segments) =>
        StoryboardCheck.Problems(output?.Segments is null ? output : output.WithPicks(), segments, maxClipSeconds: 20);

    [Fact]
    public void A_storyboard_that_covers_every_segment_within_the_limits_passes() =>
        Assert.Empty(Problems(
            new(Cast, [new("S01", [Shot(0, "Soul coin"), Shot(8, "Karlach")]), new("S02", [Shot(0)])]),
            Segment("S01", 20), Segment("S02", 10)));

    [Fact]
    public void Every_segment_needs_shots() =>
        Assert.Contains("S02 has no shots.", Problems(new(Cast, [new("S01", [Shot(0)])]), Segment("S01", 10), Segment("S02", 10)));

    [Fact]
    public void The_first_shot_starts_at_the_first_word() =>
        Assert.Contains("S01·1 must start at word 0, not word 3.", Problems(new(Cast, [new("S01", [Shot(3), Shot(10)])]), Segment("S01", 20)));

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Shots_start_later_and_later(int repeated) =>
        Assert.Contains(
            $"S01·3 starts at word {repeated}, which is not after S01·2 (word 8). Shots go in the order of the narration.",
            Problems(new(Cast, [new("S01", [Shot(0), Shot(8), Shot(repeated)])]), Segment("S01", 30)));

    [Fact]
    public void A_shot_starts_inside_its_segment() =>
        Assert.Contains("S01·2 starts at word 25, but S01 has 20 words (0 to 19).", Problems(new(Cast, [new("S01", [Shot(0), Shot(25)])]), Segment("S01", 20)));

    [Fact]
    public void A_shot_under_two_seconds_goes_back() =>
        // Cut at word 3 falls at 1.45 s.
        Assert.Contains("S01·1 lasts 1.45 s; a shot is at least 2 s.", Problems(new(Cast, [new("S01", [Shot(0), Shot(3)])]), Segment("S01", 20)));

    [Fact]
    public void A_shot_longer_than_the_video_model_makes_goes_back() =>
        // 50 words: 25 s of audio in one shot, the model makes 20 s.
        Assert.Contains("S01·1 lasts 25 s; the video model makes at most 20 s. Cut it.", Problems(new(Cast, [new("S01", [Shot(0)])]), Segment("S01", 50)));

    [Fact]
    public void A_segment_shorter_than_two_seconds_is_one_shot() =>
        Assert.Empty(Problems(new(Cast, [new("S01", [Shot(0)])]), Segment("S01", 3)));

    [Fact]
    public void More_than_four_in_a_shot_go_back()
    {
        IReadOnlyList<CastOutput> cast = [.. Enumerable.Range(1, 5).Select(i => new CastOutput($"Devil {i}", "character", "", "A devil."))];

        Assert.Contains(
            "S01·1 has 5 in it; a clip takes at most 4. Keep the 4 that matter most and describe the rest.",
            Problems(new(cast, [new("S01", [Shot(0, "Devil 1", "Devil 2", "Devil 3", "Devil 4", "Devil 5")])]), Segment("S01", 10)));
    }

    [Fact]
    public void A_shot_needs_a_description() =>
        Assert.Contains("S01·1 has no description.", Problems(new(Cast, [new("S01", [new ShotOutput(0, " ", [], false, [])])]), Segment("S01", 10)));

    [Fact]
    public void Someone_in_a_shot_must_be_on_the_cast_list() =>
        Assert.Contains("S01·1 has Raphael in it, who is not on the cast list.", Problems(new(Cast, [new("S01", [Shot(0, "Raphael")])]), Segment("S01", 10)));

    [Fact]
    public void A_cast_kind_must_be_character_place_or_object() =>
        Assert.Contains(
            "The cast list says Karlach is a \"hero\"; it must be character, place or object.",
            Problems(new([new("Karlach", "hero", "", "A look.")], [new("S01", [Shot(0)])]), Segment("S01", 10)));

    [Fact]
    public void A_segment_the_script_does_not_have_goes_back() =>
        Assert.Contains("S09 is not a segment of the script.", Problems(new(Cast, [new("S01", [Shot(0)]), new("S09", [Shot(0)])]), Segment("S01", 10)));

    [Fact]
    public void Cast_names_follow_the_cast_list_whatever_their_case_and_spaces()
    {
        var output = new StoryboardOutput(Cast, [new("S01", [Shot(0, " karlach", "SOUL COIN ")])]);

        Assert.Empty(Problems(output, Segment("S01", 10)));
        Assert.Equal(["Karlach", "Soul coin"], StoryboardCheck.ToShots(output, [Segment("S01", 10)]).Single().Cast);
    }

    [Fact]
    public void A_cut_falls_in_the_middle_of_the_pause_between_two_words()
    {
        SpokenWord[] words = [new("coin", 0.15, 1.00), new("screams", 1.20, 1.90)];

        Assert.Equal(0, ShotTiming.CutAt(words, 0));
        Assert.Equal(1.10, ShotTiming.CutAt(words, 1), 3);
    }

    [Fact]
    public void The_shots_of_a_segment_add_up_to_its_audio_and_the_last_ends_with_it()
    {
        var shots = StoryboardCheck.ToShots(new(Cast, [new("S01", [Shot(0), Shot(8), Shot(14)])]), [Segment("S01", 20)]);

        Assert.Equal([(0.0, 3.95), (3.95, 6.95), (6.95, 10.0)], shots.Select(s => (Math.Round(s.Start, 3), Math.Round(s.End, 3))));
        Assert.Equal([8, 6, 6], shots.Select(s => s.WordCount));
        Assert.Equal(10.0, shots.Sum(s => s.Seconds), 3);
    }

    [Fact]
    public void Every_shot_gets_a_key_of_its_own_that_names_its_segment()
    {
        var shots = StoryboardCheck.ToShots(new(Cast, [new("S01", [Shot(0), Shot(8)]), new("S02", [Shot(0)])]), [Segment("S01", 20), Segment("S02", 10)]);

        Assert.Equal(3, shots.Select(s => s.Key).Distinct().Count());
        Assert.All(shots, s => Assert.StartsWith(s.SegmentId + "/", s.Key));
    }

    [Fact]
    public void The_cast_list_keeps_why_something_shown_once_carries_the_story()
    {
        var cast = StoryboardCheck.ToCast(new(Cast, []));

        Assert.Equal(new CastMember("Mammon's mint", CastKind.Place, "Where every coin is made; the video turns on it.", "The look of Mammon's mint."), cast[2]);
        Assert.Equal(CastKind.Character, cast[0].Kind);
    }

    [Fact]
    public void A_rewritten_shot_is_checked_like_any_other()
    {
        var cast = StoryboardCheck.ToCast(new(Cast, []));

        Assert.Empty(StoryboardCheck.RewriteProblems(new ShotRewriteOutput("Karlach turns the coin.", ["karlach"], true, []), "S01·2", cast));
        Assert.Equal(
            ["S01·2 has no description.", "S01·2 has Raphael in it, who is not on the cast list."],
            StoryboardCheck.RewriteProblems(new ShotRewriteOutput("", ["Raphael"], false, []), "S01·2", cast));
        Assert.Equal(["The answer had no shot."], StoryboardCheck.RewriteProblems(null, "S01·2", cast));
    }

    [Fact]
    public void An_empty_answer_goes_back()
    {
        Assert.Equal(["The answer had no storyboard."], Problems(null!, Segment("S01", 10)));
    }

    [Fact]
    public void The_answer_schemas_are_plain_ASCII_for_cmd()
    {
        foreach (var json in new[] { ClaudeCliStoryboardAgent.Schema, ClaudeCliStoryboardAgent.ShotSchema })
        {
            Assert.All(json, c => Assert.True(c < 128, $"'{c}' is not plain ASCII"));
        }
    }

    [Fact]
    public void The_prompt_numbers_the_words_and_names_the_pacing_and_the_limits()
    {
        var request = new StoryboardRequest(Guid.NewGuid(), "A lore video.",
            [new(new Segment("S01", "Hook", "w0 w1 w2", ["F01"]), Segment("S01", 3))], "Prefer close-ups.", "", new Pacing(3, 5, 8), 20);

        var prompt = ClaudeCliStoryboardAgent.FirstPrompt(request);

        Assert.Contains("[0] w0 0 [1] w1 0.5 [2] w2 1", prompt);
        Assert.Contains("shots of 3 to 8 seconds, about 5 seconds", prompt);
        Assert.Contains("never longer than 20 seconds", prompt);
        Assert.Contains("Prefer close-ups.", prompt);
    }

    /// <summary>S01 with the soul coin and Karlach, S02 asking for earlier stills: for the checks on picks.</summary>
    private static EarlierStillOutput Pick(string cast, string shot) => new(cast, shot);

    private static IReadOnlyList<string> PickProblems(params EarlierStillOutput[] picks) => StoryboardCheck.Problems(
        new StoryboardOutput(Cast,
        [
            new("S01", [Shot(0, "Soul coin"), Shot(10, "Karlach")]),
            new("S02", [new ShotOutput(0, "A coin on a stone table.", ["Soul coin", "Karlach"], false, picks), Shot(10)]),
        ]),
        [Segment("S01", 20), Segment("S02", 20)], maxClipSeconds: 20);

    [Fact]
    public void A_pick_for_everyone_seen_before_passes() =>
        Assert.Empty(PickProblems(Pick("Karlach", "S01-2"), Pick("Soul coin", "S01-1")));

    [Fact]
    public void An_empty_pick_is_a_decision() =>
        Assert.Empty(PickProblems(Pick("Karlach", "S01-2"), Pick("Soul coin", "")));

    [Fact]
    public void A_cast_member_without_a_look_is_sent_back() =>
        Assert.Contains("Karlach has no look on the cast list.", StoryboardCheck.Problems(
            new StoryboardOutput([new("Karlach", "character", "", " ")], [new("S01", [Shot(0, "Karlach")])]), [Segment("S01", 10)], maxClipSeconds: 20));

    [Fact]
    public void A_look_written_on_any_entry_of_a_name_counts() =>
        Assert.Empty(StoryboardCheck.Problems(
            new StoryboardOutput([new("Karlach", "character", "", ""), new("karlach ", "character", "", "A red tiefling.")], [new("S01", [Shot(0, "Karlach")])]),
            [Segment("S01", 10)], maxClipSeconds: 20));

    [Theory]
    [InlineData("S02-2")]
    [InlineData("S02-1")]
    [InlineData("S09-1")]
    public void A_pick_of_a_shot_that_is_not_earlier_is_sent_back(string shot) =>
        Assert.Contains($"S02·1 picks the still of {shot} for Karlach, which is not an earlier shot.", PickProblems(Pick("Karlach", shot), Pick("Soul coin", "")));

    [Fact]
    public void A_pick_of_someone_not_in_the_shot_is_sent_back() =>
        Assert.Contains("S02·1 picks an earlier still for Mammon's mint, who is not in it.", PickProblems(Pick("Karlach", ""), Pick("Soul coin", ""), Pick("Mammon's mint", "S01-1")));

    [Fact]
    public void A_pick_of_a_shot_they_are_not_in_is_sent_back() =>
        Assert.Contains("S02·1 picks the still of S01-1 for Karlach, who is not in that shot.", PickProblems(Pick("Karlach", "S01-1"), Pick("Soul coin", "")));

    [Fact]
    public void A_cast_member_picked_twice_in_a_shot_is_sent_back() =>
        Assert.Contains("S02·1 picks twice for Karlach; give one.", PickProblems(Pick("Karlach", "S01-2"), Pick("karlach", ""), Pick("Soul coin", "")));

    [Fact]
    public void A_cast_member_seen_before_without_an_entry_is_sent_back() =>
        Assert.Contains(
            "S02·1 shows Soul coin, who is in S01-1 too: name the earlier shot whose still fits, or leave it empty when none does.",
            PickProblems(Pick("Karlach", "S01-2")));

    [Fact]
    public void A_pick_spelled_differently_is_the_same_cast_member()
    {
        var output = new StoryboardOutput(Cast,
        [
            new("S01", [Shot(0, "Karlach"), Shot(10)]),
            new("S02", [new ShotOutput(0, "A coin on a stone table.", ["KARLACH "], false, [new(" karlach", "s01-1")])]),
        ]);
        var segments = new[] { Segment("S01", 20), Segment("S02", 10) };

        Assert.Empty(StoryboardCheck.Problems(output, segments, maxClipSeconds: 20));
        var picked = StoryboardCheck.ToShots(output, segments, random: new Queue<string>(["0001", "0002", "0003"]).Dequeue)[2].EarlierStills;
        Assert.Equal([new EarlierStill("Karlach", "S01/0001")], picked);
    }

    [Fact]
    public void An_empty_pick_is_not_stored()
    {
        var output = new StoryboardOutput(Cast,
        [
            new("S01", [Shot(0, "Karlach")]),
            new("S02", [new ShotOutput(0, "A coin on a stone table.", ["Karlach"], false, [new("Karlach", "")])]),
        ]);

        Assert.Empty(StoryboardCheck.ToShots(output, [Segment("S01", 10), Segment("S02", 10)])[1].EarlierStills!);
    }

    [Fact]
    public void The_prompt_lists_the_facts_and_asks_for_looks_and_picks()
    {
        var request = new StoryboardRequest(Guid.NewGuid(), "A lore video.",
            [new(new Segment("S01", "Hook", "w0 w1 w2", ["F01"]), Segment("S01", 3))], "", "", new Pacing(3, 5, 8), 20,
            Facts: [new Fact("F01", "Nosulgor is blind.", "u", "q"), new Fact("F02", "He sleeps.", "u", "q", LeftOut: true)]);

        var prompt = ClaudeCliStoryboardAgent.FirstPrompt(request);

        Assert.Contains("F01: Nosulgor is blind.", prompt);
        Assert.DoesNotContain("F02", prompt);
        Assert.Contains("the size of a fist", prompt);
        Assert.Contains("Prefer the earliest shot that fits", prompt);
    }

    private static readonly IReadOnlyList<CastMember> RewriteCast = [new("Karlach", CastKind.Character, "", "A look."), new("Soul coin", CastKind.Object, "", "A look.")];

    private static Shot OnBoard(string key, int firstWord, params string[] cast) => new(key, key[..3], firstWord, 10, firstWord * 0.5, (firstWord + 10) * 0.5, "A coin.", cast, false);

    private static readonly IReadOnlyList<Shot> RewriteBoard = [OnBoard("S01/aaaa", 0, "Karlach"), OnBoard("S01/bbbb", 10, "Soul coin"), OnBoard("S01/cccc", 20, "Karlach")];

    [Fact]
    public void A_rewritten_shot_needs_a_pick_for_who_was_seen_before() =>
        Assert.Contains(
            "S01·3 shows Karlach, who is in S01-1 too: name the earlier shot whose still fits, or leave it empty when none does.",
            StoryboardCheck.RewriteProblems(new ShotRewriteOutput("A coin.", ["Karlach"], false, []), "S01·3", RewriteCast, RewriteBoard));

    [Fact]
    public void A_rewritten_shot_picks_only_from_shots_before_it() =>
        Assert.Contains(
            "S01·2 picks the still of S01-3 for Soul coin, which is not an earlier shot.",
            StoryboardCheck.RewriteProblems(new ShotRewriteOutput("A coin.", ["Soul coin"], false, [new("Soul coin", "S01-3")]), "S01·2", RewriteCast, RewriteBoard));

    [Fact]
    public void The_prompt_for_one_shot_asks_for_picks_and_shows_the_looks()
    {
        var request = new StoryboardRequest(Guid.NewGuid(), "A lore video.",
            [new(new Segment("S01", "Hook", "w", ["F01"]), Segment("S01", 30))], "", "", new Pacing(3, 5, 8), 20,
            Facts: [new Fact("F01", "Nosulgor is blind.", "u", "q")]);

        var prompt = ClaudeCliStoryboardAgent.ShotPrompt(request, RewriteBoard, RewriteCast, "S01/cccc");

        Assert.Contains("Karlach (character): A look.", prompt);
        Assert.Contains("F01: Nosulgor is blind.", prompt);
        Assert.Contains("earlierStills", prompt);
    }

    [Fact]
    public void A_new_shot_key_is_never_one_the_board_already_has()
    {
        var draws = new Queue<string>(["3f9a", "3f9a", "b071"]);
        var taken = new HashSet<string> { "S01/3f9a" };

        var key = StoryboardCheck.NewKey("S01", taken, () => draws.Dequeue());

        Assert.Equal("S01/b071", key);
        Assert.Contains("S01/b071", taken);
    }

    [Fact]
    public void Every_shot_of_a_large_board_gets_its_own_key()
    {
        var segment = Segment("S01", 2000);
        var output = new StoryboardOutput(Cast, [new("S01", [.. Enumerable.Range(0, 400).Select(i => Shot(i * 5))])]);

        var shots = StoryboardCheck.ToShots(output, [segment]);

        Assert.Equal(400, shots.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public void A_new_board_never_takes_a_key_an_earlier_board_used()
    {
        var draws = new Queue<string>(["0001", "0002", "0003"]);
        var output = new StoryboardOutput(Cast, [new("S01", [Shot(0), Shot(10)])]);

        var shots = StoryboardCheck.ToShots(output, [Segment("S01", 20)], used: ["S01/0001"], random: () => draws.Dequeue());

        Assert.Equal(["S01/0002", "S01/0003"], shots.Select(s => s.Key));
    }

    [Fact]
    public void Every_answer_schema_passes_through_cmd_unchanged()
    {
        // claude.cmd runs through cmd.exe: a number field once came out as a pattern with ^ and |.
        foreach (var schema in new[] { ClaudeCliStoryboardAgent.Schema, ClaudeCliStoryboardAgent.ShotSchema, ClaudeCliStoryboardFactCheck.Schema, ClaudeCliScriptAgent.Schema, ClaudeCliScriptAgent.SegmentSchema })
        {
            CmdShim.RequireSafe("claude.cmd", ClaudeCli.Arguments(null, [], "system.md", schema, "", null));
        }
    }

    [Fact]
    public void A_shot_starts_at_a_whole_number_in_the_schema()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(ClaudeCliStoryboardAgent.Schema);

        var firstWord = schema.RootElement.GetProperty("properties").GetProperty("segments").GetProperty("items")
            .GetProperty("properties").GetProperty("shots").GetProperty("items").GetProperty("properties").GetProperty("firstWord");

        Assert.Equal("integer", firstWord.GetProperty("type").GetString());
        Assert.False(firstWord.TryGetProperty("pattern", out _));
    }

    [Fact]
    public void A_segment_shorter_than_two_seconds_is_one_shot_not_two() =>
        // 3 words: 1.5 s of audio, cut in two.
        Assert.Contains("S01·1 lasts 0.95 s; a shot is at least 2 s.", Problems(new(Cast, [new("S01", [Shot(0), Shot(2)])]), Segment("S01", 3)));

    [Fact]
    public void The_cast_list_names_everyone_once_whatever_their_case_and_spaces()
    {
        var output = new StoryboardOutput(
            [new("Karlach", "character", "", "The look of Karlach."), new("karlach ", "character", "She carries the story.", "The look of karlach."), new("House of  Hope", "place", "", "The look of House of  Hope."), new("house of hope", "place", "", "The look of house of hope.")],
            [new("S01", [Shot(0, "House of Hope", "KARLACH")])]);

        Assert.Empty(Problems(output, Segment("S01", 10)));
        Assert.Equal(["Karlach", "House of  Hope"], StoryboardCheck.ToCast(output).Select(c => c.Name));
        Assert.Equal("She carries the story.", StoryboardCheck.ToCast(output)[0].KeyReason);
        Assert.Equal(["House of  Hope", "Karlach"], StoryboardCheck.ToShots(output, [Segment("S01", 10)]).Single().Cast);
    }

    [Fact]
    public void Empty_entries_in_an_answer_are_skipped_alike_by_the_check_and_the_shots()
    {
        var output = new StoryboardOutput(Cast, [null!, new("S01", [Shot(0), null!, Shot(10)])]);

        Assert.Empty(Problems(output, Segment("S01", 20)));
        var shots = StoryboardCheck.ToShots(output, [Segment("S01", 20)]);

        Assert.Equal([(0, 10), (10, 10)], shots.Select(s => (s.FirstWord, s.WordCount)));
    }

    [Theory]
    [InlineData("character, place")]
    [InlineData("0")]
    [InlineData("2")]
    public void A_cast_kind_is_one_of_the_three_words_not_a_number_or_a_mix(string kind) =>
        Assert.Contains(
            $"The cast list says Karlach is a \"{kind}\"; it must be character, place or object.",
            Problems(new([new("Karlach", kind, "", "A look.")], [new("S01", [Shot(0)])]), Segment("S01", 10)));
}
