using System.Net;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StoryForge.Engine.Research;
using StoryForge.ResearchServer;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The research server's one rule, that nothing but the project's sources can be reached, and the
/// answers the engine reads back out of it.
/// </summary>
public sealed class ResearchServerTests
{
    private static readonly SourceList Sources = new(["bg3.wiki", "forgottenrealms.fandom.com", "reddit.com/r/BaldursGate3"]);

    [Theory]
    [InlineData("https://bg3.wiki/wiki/Soul_Coin")]
    [InlineData("http://bg3.wiki/wiki/Soul_Coin")]
    [InlineData("https://www.bg3.wiki/")]
    [InlineData("https://BG3.WIKI/wiki/Soul_Coin")]
    [InlineData("https://forgottenrealms.fandom.com/wiki/Soul_coin?action=raw")]
    [InlineData("https://www.reddit.com/r/BaldursGate3")]
    [InlineData("https://www.reddit.com/r/BaldursGate3/comments/abc")]
    public void Pages_on_a_source_can_be_reached(string url) =>
        Assert.NotNull(Sources.Find(new Uri(url)));

    [Theory]
    [InlineData("https://evil.example/wiki/Soul_Coin")]                     // another site
    [InlineData("https://bg3.wiki.evil.example/")]                          // a source's name as a subdomain elsewhere
    [InlineData("https://evilbg3.wiki/")]                                   // ends like a source
    [InlineData("https://en.bg3.wiki/")]                                    // a subdomain of a source
    [InlineData("https://bg3.wiki@evil.example/")]                          // a source as the user name
    [InlineData("https://bg3.wiki:8443/")]                                  // another port
    [InlineData("ftp://bg3.wiki/")]                                         // not the web
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://www.reddit.com/r/BaldursGate3Memes")]              // a sibling of a narrowed source
    [InlineData("https://www.reddit.com/r/AskHistorians/")]                 // another part of a narrowed site
    [InlineData("https://www.reddit.com/")]
    public void Everything_else_is_refused(string url) =>
        Assert.Null(Sources.Find(new Uri(url)));

    [Theory]
    [InlineData("bg3.wiki", "bg3.wiki")]
    [InlineData("https://www.bg3.wiki/", "bg3.wiki")]
    [InlineData(" reddit.com/r/BaldursGate3/ ", "reddit.com/r/BaldursGate3")]
    public void Sources_are_read_however_they_were_typed(string typed, string name) =>
        Assert.Equal(name, SourceList.Parse(typed)?.Name);

    [Theory]
    [InlineData("")]
    [InlineData("not a site")]
    [InlineData("localhost")]
    [InlineData("https://bg3.wiki:8443")]
    public void Text_that_names_no_site_is_no_source(string typed) =>
        Assert.Null(SourceList.Parse(typed));

    [Fact]
    public async Task A_source_page_that_redirects_elsewhere_is_refused_at_the_redirect()
    {
        var web = new Web()
            .On("https://bg3.wiki/go", _ => Redirect("https://evil.example/landing"));
        var site = new SiteWeb(web.Invoker, Sources);

        var refused = await Assert.ThrowsAsync<RefusedException>(() => site.GetAsync(new Uri("https://bg3.wiki/go"), default));

        Assert.StartsWith(ServerAnswers.RefusedPrefix, refused.Message);
        Assert.DoesNotContain(web.Requests, r => r.Host == "evil.example");
    }

    [Fact]
    public async Task A_redirect_within_the_sources_is_followed()
    {
        var web = new Web()
            .On("https://bg3.wiki/old", _ => Redirect("/new"))
            .On("https://bg3.wiki/new", _ => Html("<p>Soul Coins are coins.</p>"));

        var page = await new SiteWeb(web.Invoker, Sources).GetAsync(new Uri("https://bg3.wiki/old"), default);

        Assert.Equal("https://bg3.wiki/new", page.Url.ToString());
    }

    [Fact]
    public async Task Fetching_a_page_off_the_list_is_refused_without_a_request()
    {
        var web = new Web();
        var tools = Tools(web);

        var result = await tools.FetchAsync("https://www.reddit.com/r/AskHistorians/", cancellationToken: default);

        Assert.True(result.IsError);
        Assert.StartsWith(ServerAnswers.RefusedPrefix, Text(result));
        Assert.Contains("bg3.wiki", Text(result));
        Assert.Empty(web.Requests);
    }

    [Fact]
    public async Task Searching_a_site_that_is_not_a_source_is_refused()
    {
        var web = new Web();

        var result = await Tools(web).SearchAsync("en.wikipedia.org", "soul coin", default);

        Assert.True(result.IsError);
        Assert.StartsWith(ServerAnswers.RefusedPrefix, Text(result));
        Assert.Empty(web.Requests);
    }

    [Fact]
    public async Task A_wiki_is_searched_through_its_API()
    {
        var web = BgWiki().On("https://bg3.wiki/w/api.php?action=query&list=search", _ => Json(
            """{"query":{"search":[{"title":"Soul Coin","snippet":"<span class=\"searchmatch\">Soul</span> Coins are small"}]}}"""));

        var result = await Tools(web).SearchAsync("bg3.wiki", "soul coin", default);

        Assert.False(result.IsError);
        Assert.Contains("\"url\": \"https://bg3.wiki/wiki/Soul_Coin\"", Text(result));
        Assert.Contains("Soul Coins are small", Text(result));
    }

    [Fact]
    public async Task An_API_address_that_leads_off_the_sources_does_not_break_the_wiki_for_the_rest_of_the_run()
    {
        // Looking for the API, /w/api.php redirects elsewhere (refused); /api.php is the real one.
        var web = new Web()
            .On("https://forgottenrealms.fandom.com/w/api.php", _ => Redirect("https://evil.example/api.php"))
            .On("https://forgottenrealms.fandom.com/api.php?action=query&meta=siteinfo", _ => Json(
                """{"query":{"general":{"server":"https://forgottenrealms.fandom.com","articlepath":"/wiki/$1"}}}"""))
            .On("https://forgottenrealms.fandom.com/api.php?action=query&list=search", _ => Json(
                """{"query":{"search":[{"title":"Soul coin","snippet":"currency of the Nine Hells"}]}}"""));
        var tools = Tools(web);

        var first = await tools.SearchAsync("forgottenrealms.fandom.com", "soul coin", default);
        var second = await tools.SearchAsync("forgottenrealms.fandom.com", "soul coin", default);

        Assert.NotEqual(true, first.IsError);
        Assert.NotEqual(true, second.IsError);
        Assert.Contains("https://forgottenrealms.fandom.com/wiki/Soul_coin", Text(second));
    }

    [Fact]
    public async Task A_site_that_is_not_a_wiki_is_asked_once_not_on_every_page()
    {
        var web = new Web()
            .On("https://www.reddit.com/r/BaldursGate3/w/api.php", _ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .On("https://www.reddit.com/w/api.php", _ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .On("https://www.reddit.com/api.php", _ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .On("https://reddit.com/w/api.php", _ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .On("https://reddit.com/api.php", _ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .On("https://www.reddit.com/r/BaldursGate3/", _ => Html("<p>Soul coins thread</p>"));
        var tools = Tools(web);

        await tools.FetchAsync("https://www.reddit.com/r/BaldursGate3/one", cancellationToken: default);
        await tools.FetchAsync("https://www.reddit.com/r/BaldursGate3/two", cancellationToken: default);

        Assert.Equal(2, web.Requests.Count(r => r.AbsolutePath.EndsWith("api.php", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_wiki_page_comes_back_as_its_text_with_its_own_URL_first()
    {
        var web = BgWiki().On("https://bg3.wiki/w/api.php?action=parse&page=Soul%20Coin", _ => Json(
            """{"parse":{"title":"Soul Coin","text":"<div><p>Soul Coins are small, coin-shaped objects.<sup class=\"reference\">[1]</sup></p><script>x()</script></div>"}}"""));

        var result = await Tools(web).FetchAsync("https://bg3.wiki/wiki/Soul_Coin", cancellationToken: default);

        var lines = Text(result).Split('\n');
        Assert.Equal(ServerAnswers.PagePrefix + "https://bg3.wiki/wiki/Soul_Coin", lines[0]);
        Assert.Contains(ServerAnswers.TextMarker, lines);
        Assert.Equal("Soul Coins are small, coin-shaped objects.", lines[^1]);
    }

    [Fact]
    public void A_long_page_comes_in_parts_that_say_where_the_next_one_starts()
    {
        var text = new string('a', ResearchTools.PageChunk + 10);

        var first = ResearchTools.Format("https://bg3.wiki/wiki/Long", null, text, 0);
        var second = ResearchTools.Format("https://bg3.wiki/wiki/Long", null, text, ResearchTools.PageChunk);

        Assert.Contains($"fetch it again with start={ResearchTools.PageChunk}", first);
        Assert.EndsWith(new string('a', 10), second);
        Assert.DoesNotContain("continues", second);
    }

    [Fact]
    public void Page_text_has_no_markup_scripts_or_edit_links()
    {
        var text = HtmlText.ToText(
            """
            <html><head><title>x</title></head><body><nav>Menu</nav>
            <main><h2>Lore<span class="mw-editsection"><span>[</span>edit<span>]</span></span></h2>
            <p>Mammon &amp; the <b>Nine</b>&nbsp;Hel&shy;ls.&#8288;</p><script>track()</script><style>p{}</style></main>
            <footer>Footer</footer></body></html>
            """);

        Assert.Equal("Lore\nMammon & the Nine Hells.", text);
    }

    [Fact]
    public void The_engine_reads_the_same_markers_the_server_writes()
    {
        Assert.Equal(Answers.RefusedPrefix, ServerAnswers.RefusedPrefix);
        Assert.Equal(Answers.PagePrefix, ServerAnswers.PagePrefix);
        Assert.Equal(Answers.TextMarker, ServerAnswers.TextMarker);
        // Claude CLI names the tools mcp__<server name in the MCP config>__<tool name>.
        Assert.Equal(ClaudeStream.SearchTool, $"mcp__{ClaudeCliResearchAgent.ServerName}__{ToolName(nameof(ResearchTools.SearchAsync))}");
        Assert.Equal(ClaudeStream.FetchTool, $"mcp__{ClaudeCliResearchAgent.ServerName}__{ToolName(nameof(ResearchTools.FetchAsync))}");
    }

    private static string? ToolName(string method) =>
        typeof(ResearchTools).GetMethod(method)!.GetCustomAttribute<McpServerToolAttribute>()!.Name;

    private static ResearchTools Tools(Web web)
    {
        var site = new SiteWeb(web.Invoker, Sources);
        return new ResearchTools(Sources, site, new MediaWiki(site));
    }

    /// <summary>bg3.wiki as MediaWiki describes itself: API under /w/, pages under /wiki/.</summary>
    private static Web BgWiki() => new Web().On("https://bg3.wiki/w/api.php?action=query&meta=siteinfo", _ => Json(
        """{"query":{"general":{"server":"https://bg3.wiki","articlepath":"/wiki/$1"}}}"""));

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers by URL prefix; anything else is refused like a closed port.</summary>
    private sealed class Web : HttpMessageHandler
    {
        private readonly List<(string Prefix, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _answers = [];

        public Web() => Invoker = new HttpMessageInvoker(this);

        public HttpMessageInvoker Invoker { get; }

        public List<Uri> Requests { get; } = [];

        public Web On(string prefix, Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            _answers.Add((prefix, answer));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var url = request.RequestUri!.AbsoluteUri;
            var match = _answers.FirstOrDefault(a => url.StartsWith(a.Prefix, StringComparison.Ordinal));
            return match.Answer is null
                ? throw new HttpRequestException("No connection could be made because the target machine actively refused it.")
                : Task.FromResult(match.Answer(request));
        }
    }
}
