using System.Net;
using StoryForge.Engine.References;
using static StoryForge.Engine.Tests.ResearchServerTests;

namespace StoryForge.Engine.Tests;

/// <summary>Pictures from the project's wikis: the page that matches a name, and the pictures themselves.</summary>
public sealed class WikiPicturesTests
{
    private static Web Wiki() => BgWiki()
        .On("https://bg3.wiki/w/api.php?action=query&list=search", _ => Json(
            """{"query":{"search":[{"title":"Karlach (origin)","snippet":"x"},{"title":"Karlach","snippet":"y"}]}}"""))
        .On("https://bg3.wiki/w/api.php?action=query&titles=Karlach&prop=pageimages", _ => Json(
            """{"query":{"pages":[{"title":"Karlach","pageimage":"Karlach.png","images":[{"title":"File:Karlach.png"}]}]}}"""))
        .On("https://bg3.wiki/w/api.php?action=query&titles=File", _ => Json(
            """{"query":{"pages":[{"title":"File:Karlach.png","imageinfo":[{"url":"https://bg3.wiki/w/images/Karlach.png","width":800,"height":1200,"mime":"image/png"}]}]}}"""))
        .On("https://bg3.wiki/w/images/Karlach.png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("image/png") })
        .On("https://bg3.wiki/w/images/Page.html", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("text/html") });

    /// <summary>Every host is a public one, except those the test names; no test asks real DNS.</summary>
    private static Task<System.Net.IPAddress[]> Public(string host, CancellationToken cancellationToken) =>
        Task.FromResult<System.Net.IPAddress[]>([host == "evil.example" ? System.Net.IPAddress.Parse("10.0.0.7") : System.Net.IPAddress.Parse("93.184.216.34")]);

    private static ByteArrayContent Picture(string type, byte[]? bytes = null)
    {
        var content = new ByteArrayContent(bytes ?? ReferencesRunTests.Png(800, 600));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task A_name_finds_the_page_called_so_and_its_pictures()
    {
        var pictures = new WikiPictures(Wiki().Invoker, Public);

        var found = await pictures.SearchAsync(["bg3.wiki"], "karlach", default);

        var portrait = Assert.Single(found);
        Assert.Equal(("https://bg3.wiki/w/images/Karlach.png", "https://bg3.wiki/wiki/Karlach", "Karlach", true), (portrait.Url, portrait.Page, portrait.PageTitle, portrait.IsMain));
    }

    [Fact]
    public async Task A_picture_downloads_with_the_ending_its_type_calls_for()
    {
        var pictures = new WikiPictures(Wiki().Invoker, Public);

        var download = await pictures.DownloadAsync("https://bg3.wiki/w/images/Karlach.png", default);

        Assert.Equal(".png", download!.Extension);
        Assert.Equal(ReferencesRunTests.Png(800, 600), download.Content);
    }

    [Theory]
    [InlineData("https://bg3.wiki/w/images/Page.html")]   // not a picture
    [InlineData("https://bg3.wiki/w/images/Missing.png")]  // does not answer
    public async Task What_is_no_picture_downloads_as_nothing(string url) =>
        Assert.Null(await new WikiPictures(Wiki().Invoker, Public).DownloadAsync(url, default));

    [Fact]
    public async Task A_source_that_is_no_wiki_finds_nothing()
    {
        var pictures = new WikiPictures(new Web()
            .On("https://example.com/", _ => new HttpResponseMessage(HttpStatusCode.NotFound)).Invoker, Public);

        Assert.Empty(await pictures.SearchAsync(["example.com"], "Karlach", default));
    }

    [Fact]
    public async Task A_fact_page_gives_its_pictures()
    {
        var found = await new WikiPictures(Wiki().Invoker, Public).PageAsync(["bg3.wiki"], "https://bg3.wiki/wiki/Karlach", default);

        Assert.Equal("https://bg3.wiki/w/images/Karlach.png", Assert.Single(found).Url);
    }

    [Theory]
    [InlineData("http://bg3.wiki/w/images/Karlach.png")]          // not https
    [InlineData("https://127.0.0.1:8188/view?filename=x.png")]   // this machine
    [InlineData("https://192.168.1.5/x.png")]                    // the local network
    [InlineData("https://[::1]/x.png")]
    [InlineData("https://evil.example/x.png")]                   // a name that leads to a private address
    [InlineData("https://[64:ff9b::c0a8:101]/x.png")]            // 192.168.1.1 through NAT64
    [InlineData("https://[2002:c0a8:101::1]/x.png")]             // 192.168.1.1 through 6to4
    [InlineData("https://[::c0a8:101]/x.png")]                   // 192.168.1.1, IPv4-compatible
    [InlineData("https://198.18.0.1/x.png")]                     // a test network
    public async Task Only_https_addresses_out_on_the_internet_are_downloaded(string url)
    {
        var address = new Uri(url).AbsoluteUri;   // "[::c0a8:101]" reads as "[::192.168.1.1]"
        var web = Wiki().On(address, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("image/png") });

        Assert.Null(await new WikiPictures(web.Invoker, Public).DownloadAsync(url, default));
        Assert.DoesNotContain(web.Requests, r => r.AbsoluteUri == address);
    }

    [Fact]
    public async Task A_redirect_is_followed_only_where_a_download_may_go()
    {
        var web = Wiki()
            .On("https://bg3.wiki/w/images/Moved.png", _ => Redirect("https://static.wikia.nocookie.net/Moved.png"))
            .On("https://static.wikia.nocookie.net/Moved.png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("image/png") })
            .On("https://bg3.wiki/w/images/Sneaky.png", _ => Redirect("https://127.0.0.1:8188/view"))
            .On("https://127.0.0.1:8188/view", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("image/png") });
        var pictures = new WikiPictures(web.Invoker, Public);

        Assert.NotNull(await pictures.DownloadAsync("https://bg3.wiki/w/images/Moved.png", default));
        Assert.Null(await pictures.DownloadAsync("https://bg3.wiki/w/images/Sneaky.png", default));
        Assert.DoesNotContain(web.Requests, r => r.Host == "127.0.0.1");
    }

    [Fact]
    public async Task What_says_it_is_a_picture_but_is_not_is_not_kept()
    {
        var web = Wiki().On("https://bg3.wiki/w/images/Fake.png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Picture("image/png", "<html>"u8.ToArray()) });

        Assert.Null(await new WikiPictures(web.Invoker, Public).DownloadAsync("https://bg3.wiki/w/images/Fake.png", default));
    }

    [Fact]
    public async Task A_wiki_answer_of_another_shape_finds_nothing_rather_than_failing()
    {
        var web = BgWiki().On("https://bg3.wiki/w/api.php?action=query&list=search", _ => Json("""{"query":{"search":"none"}}"""));

        Assert.Empty(await new WikiPictures(web.Invoker, Public).SearchAsync(["bg3.wiki"], "Karlach", default));
    }

    [Fact]
    public async Task A_wiki_is_found_once_for_all_the_names_it_is_searched_for()
    {
        var web = Wiki();
        var pictures = new WikiPictures(web.Invoker, Public);

        await pictures.SearchAsync(["bg3.wiki"], "Karlach", default);
        await pictures.SearchAsync(["bg3.wiki"], "Karlach", default);

        Assert.Single(web.Requests, r => r.Query.Contains("meta=siteinfo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_connection_goes_only_to_the_address_that_was_checked()
    {
        // The name answered with a public address when it was checked, and a local one when the connection is made.
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            WikiPictures.ConnectAsync(new DnsEndPoint("evil.example", 443), Public, default).AsTask());

        Assert.Contains("evil.example", refused.Message);
    }
}
