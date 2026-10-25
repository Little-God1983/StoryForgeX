using StoryForge.Sources;
using static StoryForge.Engine.Tests.ResearchServerTests;

namespace StoryForge.Engine.Tests;

/// <summary>A wiki page's pictures, as the References stage builds its image pool from them.</summary>
public sealed class PageImagesTests
{
    private static readonly SourceList Sources = new(["bg3.wiki"]);

    /// <summary>"Karlach" lists her portrait (the page image), a camp scene, two icons and a logo.</summary>
    private static Web KarlachPage(int scenes = 1) => BgWiki()
        .On("https://bg3.wiki/w/api.php?action=query&titles=Karlach&prop=pageimages", _ => Json(
            $$$"""
            {"query":{"pages":[{"title":"Karlach","pageimage":"Karlach_portrait.png","images":[
              {{{string.Join(",", Enumerable.Range(1, scenes).Select(i => $$$"""{"title":"File:Camp scene {{{i}}}.jpg"}"""))}}},
              {"title":"File:Karlach portrait.png"},{"title":"File:Fire icon.png"},{"title":"File:Logo.svg"}]}]}}
            """))
        .On("https://bg3.wiki/w/api.php?action=query&titles=File", request => Json(Info(request.RequestUri!)));

    /// <summary>Image info for every file the request names: icons are 64 px, the logo an SVG.</summary>
    private static string Info(Uri request)
    {
        var titles = (System.Web.HttpUtility.ParseQueryString(request.Query)["titles"] ?? "").Split('|');
        var pages = titles.Select(title =>
        {
            var name = title["File:".Length..].Replace(' ', '_');
            var (width, height, mime) = title.Contains("icon") ? (64, 64, "image/png")
                : title.EndsWith(".svg", StringComparison.Ordinal) ? (512, 512, "image/svg+xml")
                : title.Contains("portrait") ? (800, 1200, "image/png")
                : (1920, 1080, "image/jpeg");
            var caption = title["File:".Length..];
            return "{\"title\":\"" + title + "\",\"imageinfo\":[{\"url\":\"https://bg3.wiki/w/images/" + name + "\",\"width\":" + width + ",\"height\":" + height
                + ",\"mime\":\"" + mime + "\",\"extmetadata\":{\"ImageDescription\":{\"value\":\"<p>" + caption + " <b>caption</b></p>\"}}}]}";
        });
        return $$$"""{"query":{"pages":[{{{string.Join(",", pages)}}}]}}""";
    }

    private static async Task<IReadOnlyList<PageImage>> ImagesAsync(Web web, string title = "Karlach")
    {
        var wikis = new MediaWiki(new SiteWeb(web.Invoker, Sources));
        var wiki = await wikis.FindAsync(Sources.Sources[0], default);
        return await wikis.PageImagesAsync(wiki!, title, default);
    }

    [Fact]
    public async Task The_page_image_comes_first_and_icons_and_drawings_stay_out()
    {
        var images = await ImagesAsync(KarlachPage());

        Assert.Equal(["File:Karlach portrait.png", "File:Camp scene 1.jpg"], images.Select(i => i.FileTitle));
        Assert.True(images[0].IsMain);
        Assert.Equal(("https://bg3.wiki/w/images/Karlach_portrait.png", 800, 1200), (images[0].Url, images[0].Width, images[0].Height));
        Assert.Equal("Karlach portrait.png caption", images[0].Caption);
    }

    [Fact]
    public async Task At_most_six_pictures_come_from_one_page()
    {
        var images = await ImagesAsync(KarlachPage(scenes: 10));

        Assert.Equal(6, images.Count);
        Assert.Equal("File:Karlach portrait.png", images[0].FileTitle);
    }

    [Fact]
    public async Task A_page_without_pictures_or_a_missing_page_has_none()
    {
        var web = BgWiki().On("https://bg3.wiki/w/api.php?action=query&titles=Nobody", _ => Json("""{"query":{"pages":[{"title":"Nobody","missing":true}]}}"""));

        Assert.Empty(await ImagesAsync(web, "Nobody"));
    }

    /// <summary>bg3.wiki as it really answers: item icons of 196 px listed first, and an empty extmetadata written as [].</summary>
    private static Web RealKarlachPage() => BgWiki()
        .On("https://bg3.wiki/w/api.php?action=query&titles=Karlach&prop=pageimages", _ => Json(
            """{"query":{"pages":[{"title":"Karlach","pageimage":"Karlach.png","images":[{"title":"File:Alchemy Pouch Item Icon.png"},{"title":"File:Boots Icon.png"},{"title":"File:Bracers Icon.png"},{"title":"File:Cloak Icon.png"},{"title":"File:Gloves Icon.png"},{"title":"File:Helmet Icon.png"},{"title":"File:Karlach.png"},{"title":"File:Karlach camp.jpg"}]}]}}"""))
        .On("https://bg3.wiki/w/api.php?action=query&titles=File", request => Json(RealInfo(request.RequestUri!)));

    private static string RealInfo(Uri request)
    {
        var titles = (System.Web.HttpUtility.ParseQueryString(request.Query)["titles"] ?? "").Split('|');
        var pages = titles.Select(title =>
        {
            var (width, height) = title.Contains("Icon") ? (196, 196) : title.EndsWith(".jpg", StringComparison.Ordinal) ? (1920, 1080) : (997, 1148);
            return "{\"title\":\"" + title + "\",\"imageinfo\":[{\"url\":\"https://bg3.wiki/w/images/" + title[5..].Replace(' ', '_') + "\",\"width\":" + width
                + ",\"height\":" + height + ",\"mime\":\"" + (title.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "image/png") + "\",\"extmetadata\":[]}]}";
        });
        return "{\"query\":{\"pages\":[" + string.Join(",", pages) + "]}}";
    }

    [Fact]
    public async Task A_file_without_metadata_has_no_caption()
    {
        var images = await ImagesAsync(RealKarlachPage());

        Assert.Equal("", images[0].Caption);
    }

    [Fact]
    public async Task After_the_main_picture_the_biggest_come_first_so_icons_do_not_fill_the_pool()
    {
        var images = await ImagesAsync(RealKarlachPage());

        Assert.Equal(["File:Karlach.png", "File:Karlach camp.jpg"], images.Take(2).Select(i => i.FileTitle));
    }

    [Fact]
    public async Task On_a_wiki_that_names_its_files_otherwise_the_page_image_still_comes_first()
    {
        var web = BgWiki()
            .On("https://bg3.wiki/w/api.php?action=query&titles=Karlach&prop=pageimages", _ => Json(
                """{"query":{"pages":[{"title":"Karlach","pageimage":"Karlach_portrait.png","images":[{"title":"Datei:Camp scene.jpg"},{"title":"Datei:Karlach portrait.png"}]}]}}"""))
            .On("https://bg3.wiki/w/api.php?action=query&titles=Datei", _ => Json(
                """{"query":{"pages":[{"title":"Datei:Camp scene.jpg","imageinfo":[{"url":"https://bg3.wiki/w/images/Camp.jpg","width":1920,"height":1080,"mime":"image/jpeg","extmetadata":[]}]},{"title":"Datei:Karlach portrait.png","imageinfo":[{"url":"https://bg3.wiki/w/images/Karlach.png","width":800,"height":1200,"mime":"image/png","extmetadata":[]}]}]}}"""));

        var images = await ImagesAsync(web);

        Assert.Equal(("Datei:Karlach portrait.png", true), (images[0].FileTitle, images[0].IsMain));
    }

    [Fact]
    public async Task Pictures_past_the_first_fifty_files_are_found_too()
    {
        // Item icons sort first; the one real picture comes after them.
        var files = Enumerable.Range(1, 55).Select(i => $$$"""{"title":"File:Aa {{{i:00}}} icon.png"}""").Append("""{"title":"File:Zz portrait.png"}""");
        var web = BgWiki()
            .On("https://bg3.wiki/w/api.php?action=query&titles=Karlach&prop=pageimages", _ => Json(
                $$$"""{"query":{"pages":[{"title":"Karlach","images":[{{{string.Join(",", files)}}}]}]}}"""))
            .On("https://bg3.wiki/w/api.php?action=query&titles=File", request => Json(Info(request.RequestUri!)));

        var images = await ImagesAsync(web);

        Assert.Equal("File:Zz portrait.png", Assert.Single(images).FileTitle);
    }
}
