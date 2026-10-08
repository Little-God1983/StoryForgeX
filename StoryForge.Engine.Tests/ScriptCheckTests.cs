using System.Text.Json;
using StoryForge.Client;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;

namespace StoryForge.Engine.Tests;

/// <summary>How the engine checks a script against the approved fact sheet, and what it asks the model.</summary>
public sealed class ScriptCheckTests
{
    private const string Url = "https://bg3.wiki/wiki/Soul_Coin";

    private static readonly FactSheet Sheet = new(
    [
        new Fact("F01", "Soul coins hold one trapped soul.", Url, "q", Weight: 10),
        new Fact("F02", "Soul coins are money in the Nine Hells.", Url, "q"),
        new Fact("F03", "Infernal war machines burn soul coins.", Url, "q", LeftOut: true),
        new Fact("F04", "There are 19 soul coins in the game.", Url, "q", Weight: 1),
    ]);

    /// <summary>Narration of about <paramref name="seconds"/> seconds in English (150 words a minute).</summary>
    private static string Words(int seconds) => string.Join(' ', Enumerable.Repeat("word", seconds * 150 / 60));

    private static ScriptPart Part(string title, int seconds, params string[] facts) => new(title, Words(seconds), facts);

    private static IReadOnlyList<string> Problems(ScriptOutput output, int target = 60) =>
        ScriptCheck.Problems(output, Sheet, "English", target);

    [Fact]
    public void A_script_from_the_facts_in_use_with_every_must_fact_passes() =>
        Assert.Empty(Problems(new([Part("Hook - a coin that screams", 20, "F01"), Part("Money of Hell", 30, "F02", "F04")], "")));

    [Fact]
    public void Facts_that_are_not_on_the_sheet_or_were_left_out_are_sent_back()
    {
        var problems = Problems(new([Part("Hook", 20, "F01", "F99"), Part("War", 30, "F03")], ""));

        Assert.Equal(
            ["S01 uses F99, which is not on the fact sheet.", "S02 uses F03, which was left out. Use only the facts in the list."],
            problems);
    }

    [Fact]
    public void Every_fact_marked_must_has_to_be_used() =>
        Assert.Equal(["F01 is marked must, but no segment uses it."], Problems(new([Part("Money", 50, "F02")], "")));

    [Fact]
    public void A_segment_needs_a_title_a_narration_and_its_facts()
    {
        var problems = Problems(new([null!, new ScriptPart(" ", "", []), Part("Hook", 50, "F01")], ""));

        Assert.Equal(["S01 is empty.", "S02 has no title.", "S02 has no narration.", "S02 names no facts. Every segment says which facts it uses."], problems);
    }

    [Fact]
    public void Too_long_needs_a_reason_and_with_one_it_passes()
    {
        ScriptOutput Long(string note) => new([Part("Hook", 60, "F01"), Part("Money", 60, "F02")], note);

        Assert.StartsWith("The script runs about 2:00; the target is 1:00. Shorten it", Problems(Long("")).Single());
        Assert.Empty(Problems(Long("The facts marked must need about 2:00.")));
    }

    [Fact]
    public void Too_short_goes_back_only_while_facts_are_left_unused()
    {
        Assert.StartsWith("The script runs about 0:20; the target is 1:00. Use more of the 2 unused facts", Problems(new([Part("Hook", 20, "F01")], "")).Single());
        Assert.Empty(Problems(new([Part("Hook", 20, "F01", "F02", "F04")], "")));
    }

    [Fact]
    public void A_checked_script_becomes_numbered_segments()
    {
        var script = ScriptCheck.ToSheet(new([new(" Hook ", " Narration. ", ["F01", " F01 "]), new("Outro", "Bye.", ["F02"])], " "), factsVersion: 3);

        Assert.Equal(["S01", "S02"], script.Segments.Select(s => s.Id));
        Assert.Equal(("Hook", "Narration."), (script.Segments[0].Title, script.Segments[0].Narration));
        Assert.Equal(["F01"], script.Segments[0].FactIds);
        Assert.Equal("", script.LengthNote);
        Assert.Equal(3, script.FactsVersion);
    }

    [Fact]
    public void A_rewritten_segment_keeps_the_must_facts_no_other_segment_carries()
    {
        IReadOnlyList<Segment> script = [new("S01", "Hook", "x", ["F01"]), new("S02", "Money", "y", ["F02"])];

        Assert.Equal(
            ["F01 is marked must and no other segment uses it, so S01 has to keep it."],
            ScriptCheck.SegmentProblems(Part("Hook", 10, "F04"), "S01", Sheet, script));
        Assert.Empty(ScriptCheck.SegmentProblems(Part("Money", 10, "F04"), "S02", Sheet, script));
    }

    [Fact]
    public void A_must_fact_no_segment_uses_is_not_asked_of_a_rewrite()
    {
        // S01 was switched back to a version without F01: no segment carries it any more.
        IReadOnlyList<Segment> script = [new("S01", "Hook", "x", ["F02"]), new("S02", "Money", "y", ["F02"])];

        Assert.Empty(ScriptCheck.SegmentProblems(Part("Money", 10, "F04"), "S02", Sheet, script));
    }

    [Fact]
    public void Fact_ids_with_spaces_around_them_still_count()
    {
        IReadOnlyList<Segment> script = [new("S01", "Hook", "x", ["F01"]), new("S02", "Money", "y", ["F02"])];

        Assert.Empty(Problems(new([Part("Hook", 20, " F01 "), Part("Money", 30, "F02")], "")));
        Assert.Empty(ScriptCheck.SegmentProblems(Part("Hook", 10, "F01 "), "S01", Sheet, script));
    }

    [Theory]
    [InlineData("English", 150, 60)]
    [InlineData("German", 130, 60)]
    [InlineData("Klingon", 140, 60)]
    public void Narration_is_timed_by_the_words_a_minute_of_its_language(string language, int words, int seconds) =>
        Assert.Equal(seconds, ScriptSheet.Seconds(string.Join(' ', Enumerable.Repeat("w", words)), language));

    [Fact]
    public void The_schemas_ask_for_title_narration_and_facts_and_survive_claude_cmd()
    {
        using var schema = JsonDocument.Parse(ClaudeCliScriptAgent.Schema);
        var segment = schema.RootElement.GetProperty("properties").GetProperty("segments").GetProperty("items");

        Assert.Equal(["segments", "lengthNote"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["title", "narration", "factIds"], segment.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        foreach (var json in new[] { ClaudeCliScriptAgent.Schema, ClaudeCliScriptAgent.SegmentSchema })
        {
            Assert.All(json, c => Assert.True(c < 128, $"'{c}' is not plain ASCII"));
            CmdShim.RequireSafe(@"C:\npm\claude.cmd", ["--json-schema", json]);
        }
    }

    [Fact]
    public void The_script_writer_has_no_tools_and_no_servers()
    {
        var arguments = ClaudeCli.Arguments(null, [], "system.md", ClaudeCliScriptAgent.Schema, "", null);

        Assert.Equal("", arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.DoesNotContain("--allowedTools", arguments);
    }

    [Fact]
    public void The_prompt_lists_only_the_facts_in_use_with_their_weights()
    {
        var prompt = ClaudeCliScriptAgent.FirstPrompt(new ScriptRequest(Guid.NewGuid(), "Soul coins in BG3.", Sheet, "Keep it eerie.", "", "English", 240));

        Assert.Contains("about 4:00 in English: about 600 words", prompt);
        Assert.Contains("F01 [10, must] Soul coins hold one trapped soul.", prompt);
        Assert.Contains("F04 [1] There are 19 soul coins in the game.", prompt);
        Assert.DoesNotContain("F03", prompt);   // left out: never shown
        Assert.Contains("Keep it eerie.", prompt);
    }

    [Fact]
    public void Writing_one_segment_again_shows_the_whole_script_and_what_it_must_keep()
    {
        IReadOnlyList<Segment> script = [new("S01", "Hook", "A coin screams.", ["F01"]), new("S02", "Money", "Hell pays in souls.", ["F02"])];

        var prompt = ClaudeCliScriptAgent.SegmentPrompt(new ScriptRequest(Guid.NewGuid(), "b", Sheet, "", "", "English", 240), script, "S01");

        Assert.Contains("S02 Money (facts F02):\nHell pays in souls.", prompt.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("Write S01 again", prompt);
        Assert.Contains("It must keep F01", prompt);
    }
}
