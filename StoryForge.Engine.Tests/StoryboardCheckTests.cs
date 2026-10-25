using StoryForge.Client;
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
        new("Karlach", "character", ""),
        new("Soul coin", "object", ""),
        new("Mammon's mint", "place", "Where every coin is made; the video turns on it."),
    ];

    private static ShotOutput Shot(int firstWord, params string[] cast) => new(firstWord, "A coin on a stone table.", cast, false);

    private static IReadOnlyList<string> Problems(StoryboardOutput output, params SegmentWords[] segments) =>
        StoryboardCheck.Problems(output, segments, maxClipSeconds: 20);

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
        IReadOnlyList<CastOutput> cast = [.. Enumerable.Range(1, 5).Select(i => new CastOutput($"Devil {i}", "character", ""))];

        Assert.Contains(
            "S01·1 has 5 in it; a clip takes at most 4. Keep the 4 that matter most and describe the rest.",
            Problems(new(cast, [new("S01", [Shot(0, "Devil 1", "Devil 2", "Devil 3", "Devil 4", "Devil 5")])]), Segment("S01", 10)));
    }

    [Fact]
    public void A_shot_needs_a_description() =>
        Assert.Contains("S01·1 has no description.", Problems(new(Cast, [new("S01", [new ShotOutput(0, " ", [], false)])]), Segment("S01", 10)));

    [Fact]
    public void Someone_in_a_shot_must_be_on_the_cast_list() =>
        Assert.Contains("S01·1 has Raphael in it, who is not on the cast list.", Problems(new(Cast, [new("S01", [Shot(0, "Raphael")])]), Segment("S01", 10)));

    [Fact]
    public void A_cast_kind_must_be_character_place_or_object() =>
        Assert.Contains(
            "The cast list says Karlach is a \"hero\"; it must be character, place or object.",
            Problems(new([new("Karlach", "hero", "")], [new("S01", [Shot(0)])]), Segment("S01", 10)));

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

        Assert.Equal(new CastMember("Mammon's mint", CastKind.Place, "Where every coin is made; the video turns on it."), cast[2]);
        Assert.Equal(CastKind.Character, cast[0].Kind);
    }

    [Fact]
    public void A_rewritten_shot_is_checked_like_any_other()
    {
        var cast = StoryboardCheck.ToCast(new(Cast, []));

        Assert.Empty(StoryboardCheck.RewriteProblems(new ShotRewriteOutput("Karlach turns the coin.", ["karlach"], true), "S01·2", cast));
        Assert.Equal(
            ["S01·2 has no description.", "S01·2 has Raphael in it, who is not on the cast list."],
            StoryboardCheck.RewriteProblems(new ShotRewriteOutput("", ["Raphael"], false), "S01·2", cast));
        Assert.Equal(["The answer had no shot."], StoryboardCheck.RewriteProblems(null, "S01·2", cast));
    }

    [Fact]
    public void An_empty_answer_goes_back()
    {
        Assert.Equal(["The answer had no storyboard."], Problems(null!, Segment("S01", 10)));
    }
}
