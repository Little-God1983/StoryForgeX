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

    private static ByteArrayContent Picture(string type)
    {
        var content = new ByteArrayContent([137, 80, 78, 71]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task A_name_finds_the_page_called_so_and_its_pictures()
    {
        var pictures = new WikiPictures(Wiki());

        var found = await pictures.SearchAsync(["bg3.wiki"], "karlach", default);

        var portrait = Assert.Single(found);
        Assert.Equal(("https://bg3.wiki/w/images/Karlach.png", "https://bg3.wiki/wiki/Karlach", "Karlach", true), (portrait.Url, portrait.Page, portrait.PageTitle, portrait.IsMain));
    }

    [Fact]
    public async Task A_picture_downloads_with_the_ending_its_type_calls_for()
    {
        var pictures = new WikiPictures(Wiki());

        var download = await pictures.DownloadAsync("https://bg3.wiki/w/images/Karlach.png", default);

        Assert.Equal(".png", download!.Extension);
        Assert.Equal([137, 80, 78, 71], download.Content);
    }

    [Theory]
    [InlineData("https://bg3.wiki/w/images/Page.html")]   // not a picture
    [InlineData("https://bg3.wiki/w/images/Missing.png")]  // does not answer
    public async Task What_is_no_picture_downloads_as_nothing(string url) =>
        Assert.Null(await new WikiPictures(Wiki()).DownloadAsync(url, default));

    [Fact]
    public async Task A_source_that_is_no_wiki_finds_nothing()
    {
        var pictures = new WikiPictures(new Web()
            .On("https://example.com/", _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Empty(await pictures.SearchAsync(["example.com"], "Karlach", default));
    }

    [Fact]
    public async Task A_fact_page_gives_its_pictures()
    {
        var found = await new WikiPictures(Wiki()).PageAsync(["bg3.wiki"], "https://bg3.wiki/wiki/Karlach", default);

        Assert.Equal("https://bg3.wiki/w/images/Karlach.png", Assert.Single(found).Url);
    }
}
