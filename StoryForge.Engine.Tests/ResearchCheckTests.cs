using System.Text.Json;
using StoryForge.Client;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Tests;

/// <summary>
/// How the engine checks a fact sheet against what was really read, and how it reads Claude CLI's
/// output while the research runs.
/// </summary>
public sealed class ResearchCheckTests
{
    private const string Page = "https://bg3.wiki/wiki/Soul_Coin";

    private const string PageText =
        "Soul Coins are small, coin-shaped objects forged of infernal iron into which a single mortal soul is bound. " +
        "They are used as currency in the Nine Hells and can power infernal engines such as the one in Karlach’s chest.";

    private static ResearchPages Read()
    {
        var pages = new ResearchPages();
        pages.Add(Page, null, PageText);
        return pages;
    }

    private static ResearchOutput Output(params ResearchFact[] facts) => new(facts);

    [Theory]
    [InlineData("They are used as currency in the Nine Hells")]
    [InlineData("they are USED as   currency\nin the nine hells")]                          // case and spacing
    [InlineData("such as the one in Karlach's chest")]                                     // a straight apostrophe
    [InlineData("Soul Coins are small, coin-shaped objects … used as currency")]           // shortened with …
    [InlineData("Soul Coins are small, coin-shaped objects ... used as currency")]
    public void A_quote_that_is_on_the_page_passes(string quote) =>
        Assert.Empty(FactSheetCheck.Problems(Output(new ResearchFact("Coins hold souls.", Page, quote)), Read()));

    [Theory]
    [InlineData("https://www.bg3.wiki/wiki/Soul%20Coin")]
    [InlineData("http://bg3.wiki/wiki/Soul_Coin/")]
    public void The_same_page_written_another_way_counts_as_read(string url) =>
        Assert.Empty(FactSheetCheck.Problems(Output(new ResearchFact("Coins hold souls.", url, "a single mortal soul is bound")), Read()));

    [Fact]
    public void A_page_reached_through_a_redirect_counts_under_both_addresses()
    {
        var pages = new ResearchPages();
        pages.Add(Page, "https://bg3.wiki/wiki/Soul_Coins", PageText);

        Assert.True(pages.WasRead("https://bg3.wiki/wiki/Soul_Coins"));
        Assert.True(pages.Contains("https://bg3.wiki/wiki/Soul_Coins", "a single mortal soul is bound"));
    }

    [Fact]
    public void A_quote_that_is_not_on_the_page_is_sent_back()
    {
        var problems = FactSheetCheck.Problems(
            Output(new ResearchFact("Coins hold souls.", Page, "Soul coins were minted by Mammon himself.")), Read());

        Assert.Equal(["F01: the quote is not on https://bg3.wiki/wiki/Soul_Coin as it was read. Copy the passage word for word."], problems);
    }

    [Fact]
    public void A_source_that_was_never_read_is_sent_back_even_when_it_is_real()
    {
        var problems = FactSheetCheck.Problems(
            Output(new ResearchFact("Coins fuel war machines.", "https://forgottenrealms.fandom.com/wiki/Soul_coin", "fuel")), Read());

        Assert.Single(problems);
        Assert.StartsWith("F01 points to https://forgottenrealms.fandom.com/wiki/Soul_coin, which was not read", problems[0]);
    }

    [Fact]
    public void Missing_parts_are_named_per_fact()
    {
        var problems = FactSheetCheck.Problems(Output(
            new ResearchFact("Fine.", Page, "a single mortal soul is bound"),
            new ResearchFact(" ", Page, "a single mortal soul is bound"),
            new ResearchFact("No source.", "", "x"),
            new ResearchFact("No quote.", Page, "")), Read());

        Assert.Equal(["F02 has no statement.", "F03 has no source link.", "F04 has no quote."], problems);
    }

    [Fact]
    public void No_answer_or_no_facts_is_a_problem()
    {
        Assert.Equal(["The answer had no fact sheet."], FactSheetCheck.Problems(null, Read()));
        Assert.Equal(["The fact sheet has no facts."], FactSheetCheck.Problems(Output(), Read()));
        Assert.StartsWith("The fact sheet has no facts, and no page was read", FactSheetCheck.Problems(Output(), new ResearchPages())[0]);
    }

    [Fact]
    public void A_checked_answer_becomes_a_numbered_sheet_at_the_default_weight()
    {
        var sheet = FactSheetCheck.ToSheet(Output(
            new ResearchFact(" Coins hold souls. ", Page, " a single mortal soul is bound "),
            new ResearchFact("Coins are money in Hell.", Page, "currency in the Nine Hells")));

        Assert.Equal(
            [
                new Fact("F01", "Coins hold souls.", Page, "a single mortal soul is bound", Fact.DefaultWeight, LeftOut: false),
                new Fact("F02", "Coins are money in Hell.", Page, "currency in the Nine Hells", 5, false),
            ],
            sheet.Facts);
    }

    [Fact]
    public void The_schema_requires_statement_source_and_quote_for_every_fact()
    {
        using var schema = JsonDocument.Parse(ResearchSchema.Json);
        var fact = schema.RootElement.GetProperty("properties").GetProperty("facts").GetProperty("items");

        Assert.Equal(["facts"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["statement", "sourceUrl", "quote"], fact.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(fact.GetProperty("properties").GetProperty("quote").TryGetProperty("description", out _));
    }

    [Fact]
    public void Claude_gets_the_research_tools_and_nothing_else()
    {
        var arguments = ClaudeCliResearchAgent.Arguments(@"C:\data\mcp.json", @"C:\data\system.md", "sonnet", session: null);

        Assert.Equal("", After(arguments, "--tools"));                 // no built-in tools: no web search, files or shell
        Assert.Contains("--strict-mcp-config", arguments);              // none of the user's MCP servers
        Assert.Equal("", After(arguments, "--setting-sources"));       // none of the user's settings, hooks or plugins
        Assert.Equal($"{ClaudeStream.SearchTool},{ClaudeStream.FetchTool}", After(arguments, "--allowedTools"));
        Assert.Equal("none", After(arguments, "--permission-prompts"));
        Assert.Equal(ResearchSchema.Json, After(arguments, "--json-schema"));
        Assert.Equal("sonnet", After(arguments, "--model"));
        Assert.DoesNotContain("--resume", arguments);
    }

    [Fact]
    public void A_correction_continues_the_same_session()
    {
        var arguments = ClaudeCliResearchAgent.Arguments("mcp.json", "system.md", "", session: "abc");

        Assert.Equal("abc", After(arguments, "--resume"));
        Assert.DoesNotContain("--model", arguments);
    }

    [Fact]
    public void Every_argument_survives_claude_cmd()
    {
        // npm installs Claude CLI as claude.cmd, and cmd.exe reads % ^ & | < > itself.
        CmdShim.RequireSafe(@"C:\npm\claude.cmd", ClaudeCliResearchAgent.Arguments(@"C:\Users\Little God\mcp.json", "system.md", "sonnet", "abc"));

        var refused = Assert.Throws<ArgumentException>(() => CmdShim.RequireSafe(@"C:\npm\claude.cmd", ["--model", "a&calc"]));
        Assert.Contains("a&calc", refused.Message);
        CmdShim.RequireSafe(@"C:\bin\claude.exe", ["--model", "a&b"]);   // an exe gets its arguments as they are
    }

    [Fact]
    public void The_server_is_started_with_the_projects_sources()
    {
        using var config = JsonDocument.Parse(ClaudeCliResearchAgent.McpConfig(@"C:\app\StoryForge.ResearchServer.exe", ["bg3.wiki", "reddit.com/r/BaldursGate3"]));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty(ClaudeCliResearchAgent.ServerName);

        Assert.Equal(@"C:\app\StoryForge.ResearchServer.exe", server.GetProperty("command").GetString());
        Assert.Equal(["--source", "bg3.wiki", "--source", "reddit.com/r/BaldursGate3"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void The_prompt_names_the_brief_the_sources_and_the_profile()
    {
        var prompt = ClaudeCliResearchAgent.FirstPrompt(new ResearchRequest(
            Guid.NewGuid(), "Soul coins in BG3.", ["bg3.wiki"], "Dates matter.", "", "English", 240));

        Assert.Contains("about 4:00 in English", prompt);
        Assert.Contains("Soul coins in BG3.", prompt);
        Assert.Contains("- bg3.wiki", prompt);
        Assert.Contains("Dates matter.", prompt);
    }

    [Fact]
    public void The_stream_becomes_activity_pages_and_the_answer()
    {
        var pages = new ResearchPages();
        var activity = new List<ActivityLine>();
        var stream = new ClaudeStream(pages, new Collect(activity), TimeProvider.System);

        foreach (var line in ClaudeOutput)
        {
            stream.Read(line);
        }

        Assert.Equal(
            [
                (ActivityKind.Refused, "www.reddit.com/r/BaldursGate3/  (not a project source)"),
                (ActivityKind.Search, "bg3.wiki  \"soul coin\"  → 2 hits"),
                (ActivityKind.Fetch, "bg3.wiki/wiki/Soul_Coin"),
                (ActivityKind.Failed, "bg3.wiki/wiki/Nope  – bg3.wiki has no page \"Nope\"."),
                (ActivityKind.Model, "writing the fact sheet"),
            ],
            activity.Select(a => (a.Kind, a.Text)));
        Assert.True(pages.Contains(Page, "a single mortal soul is bound"));
        Assert.Equal("s-1", stream.Session);
        Assert.Null(stream.ServerProblem);
        Assert.Equal(0.04m, stream.CostUsd);
        Assert.Equal("Coins hold souls.", ResearchSchema.Read(stream.Output!.Value)!.Facts[0].Statement);
    }

    [Fact]
    public void A_research_server_that_did_not_start_is_reported()
    {
        var stream = new ClaudeStream(new ResearchPages(), new Collect([]), TimeProvider.System);

        stream.Read("""{"type":"system","subtype":"init","session_id":"s","mcp_servers":[{"name":"storyforge","status":"failed"}]}""");

        Assert.Equal("failed", stream.ServerProblem);
    }

    [Fact]
    public void An_error_result_is_kept_as_the_reason()
    {
        var stream = new ClaudeStream(new ResearchPages(), new Collect([]), TimeProvider.System);

        stream.Read("not json at all");
        stream.Read("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Invalid API key","session_id":"s"}""");

        Assert.Null(stream.Output);
        Assert.Equal("Invalid API key", stream.Error);
    }

    private static string? After(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    /// <summary>Shortened from a real run: a refusal, a search, a page, a missing page, the answer.</summary>
    private static readonly string[] ClaudeOutput =
    [
        """{"type":"system","subtype":"init","session_id":"s-1","tools":["StructuredOutput","mcp__storyforge__fetch","mcp__storyforge__search"],"mcp_servers":[{"name":"storyforge","status":"connected"}]}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__storyforge__search","input":{"source":"bg3.wiki","query":"soul coin"}},{"type":"tool_use","id":"t2","name":"mcp__storyforge__fetch","input":{"url":"https://www.reddit.com/r/BaldursGate3/"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":[{"type":"text","text":"Refused: https://www.reddit.com/r/BaldursGate3/ is not on the project's source list (bg3.wiki)."}]}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"{\"source\":\"bg3.wiki\",\"results\":[{\"title\":\"Soul Coin\"},{\"title\":\"Nadira\"}]}"}]}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"mcp__storyforge__fetch","input":{"url":"https://bg3.wiki/wiki/Soul_Coin"}},{"type":"tool_use","id":"t4","name":"mcp__storyforge__fetch","input":{"url":"https://bg3.wiki/wiki/Nope"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"Page: https://bg3.wiki/wiki/Soul_Coin\nTitle: Soul Coin\nCharacters 0–100 of 100.\n-----\nSoul Coins are small, coin-shaped objects forged of infernal iron into which a single mortal soul is bound."}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t4","is_error":true,"content":"bg3.wiki has no page \"Nope\"."}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t5","name":"StructuredOutput","input":{}}]}}""",
        """{"type":"result","subtype":"success","is_error":false,"session_id":"s-1","total_cost_usd":0.04,"structured_output":{"facts":[{"statement":"Coins hold souls.","sourceUrl":"https://bg3.wiki/wiki/Soul_Coin","quote":"a single mortal soul is bound"}]}}""",
    ];

    private sealed class Collect(List<ActivityLine> lines) : IProgress<ActivityLine>
    {
        public void Report(ActivityLine value) => lines.Add(value);
    }
}
