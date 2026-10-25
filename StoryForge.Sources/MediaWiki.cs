using System.Collections.Concurrent;
using System.Text.Json;

namespace StoryForge.Sources;

/// <summary>A source that runs MediaWiki (bg3.wiki, every Fandom wiki): where its API is and how its page URLs look.</summary>
/// <param name="ArticleUrl">"https://bg3.wiki/wiki/$1": a page title goes where $1 is.</param>
internal sealed record WikiSite(Source Source, Uri Api, string ArticleUrl);

internal sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>A picture a wiki page shows, e.g. Karlach's portrait: where the wiki serves it, its size and caption.</summary>
/// <param name="FileTitle">"File:Karlach portrait.png".</param>
/// <param name="IsMain">The page's own picture (MediaWiki's page image).</param>
internal sealed record PageImage(string Url, string FileTitle, int Width, int Height, string Caption, bool IsMain);

/// <summary>A page as the reader sees it, with the title the wiki gives it (after any redirect).</summary>
internal sealed record WikiPage(string Title, string Url, string Text);

/// <summary>
/// Search and page text through the MediaWiki API. Wikis answer it reliably and without the menus,
/// ads and scripts their HTML pages carry.
/// </summary>
internal sealed class MediaWiki(SiteWeb web)
{
    // bg3.wiki keeps its API under /w/, Fandom at the root.
    private static readonly string[] ApiPaths = ["/w/api.php", "/api.php"];

    private readonly ConcurrentDictionary<Source, Task<Discovery>> _sites = new();

    /// <summary>The wiki behind <paramref name="source"/>, or null when it is not a MediaWiki site.</summary>
    public async Task<WikiSite?> FindAsync(Source source, CancellationToken cancellationToken)
    {
        // Shared by every call, so no single call's token; each request has its own timeout.
        var lookup = _sites.GetOrAdd(source, s => DiscoverAsync(s, CancellationToken.None));
        Discovery found;
        try
        {
            found = await lookup.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lookup.IsFaulted)
        {
            // Not meant to happen (DiscoverAsync catches what it expects), but a lookup that broke
            // must not stay cached and break every later call on this source.
            _sites.TryRemove(source, out _);
            throw;
        }
        if (!found.Settled)
        {
            // The site did not answer: maybe it was down for a moment, so the next call asks again.
            // "Not a wiki" is settled and kept, so a plain site is not asked again for every page.
            _sites.TryRemove(source, out _);
        }
        return found.Site;
    }

    /// <summary>What looking for a wiki found; unsettled when the site could not be reached.</summary>
    private sealed record Discovery(WikiSite? Site, bool Settled);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(WikiSite wiki, string query, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(wiki, $"action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit=10&srprop=snippet", cancellationToken);
        if (!json.RootElement.TryGetProperty("query", out var result) || !result.TryGetProperty("search", out var hits))
        {
            return [];
        }
        return
        [
            .. hits.EnumerateArray()
                .Select(hit => (Title: hit.GetProperty("title").GetString() ?? "", Snippet: hit.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : ""))
                .Select(hit => new SearchHit(hit.Title, PageUrl(wiki, hit.Title), HtmlText.ToText(hit.Snippet)))
                .Where(hit => wiki.Source.Contains(new Uri(hit.Url))),
        ];
    }

    /// <summary>The page title a URL on this wiki points at ("/wiki/Soul_Coin" → "Soul Coin"); null for other pages.</summary>
    public static string? TitleOf(WikiSite wiki, Uri url)
    {
        var prefix = new Uri(wiki.ArticleUrl.Replace("$1", "", StringComparison.Ordinal));
        if (url.AbsolutePath.StartsWith(prefix.AbsolutePath, StringComparison.Ordinal) && url.AbsolutePath.Length > prefix.AbsolutePath.Length)
        {
            return Uri.UnescapeDataString(url.AbsolutePath[prefix.AbsolutePath.Length..]).Replace('_', ' ');
        }
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["title"] is { Length: > 0 } title ? title.Replace('_', ' ') : null;
    }

    public async Task<WikiPage> PageAsync(WikiSite wiki, string title, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(wiki, $"action=parse&page={Uri.EscapeDataString(title)}&prop=text&redirects=1&disableeditsection=1&disabletoc=1", cancellationToken);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var info = error.TryGetProperty("info", out var i) ? i.GetString() : null;
            throw new WebFailureException($"{wiki.Source.Name} has no page \"{title}\"{(info is null ? "" : $": {info}")}.");
        }
        var parse = json.RootElement.GetProperty("parse");
        var actual = parse.GetProperty("title").GetString() ?? title;
        return new WikiPage(actual, PageUrl(wiki, actual), HtmlText.ToText(parse.GetProperty("text").GetString() ?? ""));
    }

    /// <summary>The most a page gives the image pool: its main picture and a few more.</summary>
    public const int MaxPageImages = 6;

    /// <summary>Smaller than this on either side is an icon or a sprite, not a picture of anything.</summary>
    public const int MinImageSide = 128;

    private static readonly string[] PictureTypes = ["image/png", "image/jpeg", "image/webp"];

    // The file endings those are saved as: only such files are asked about.
    private static readonly HashSet<string> PictureEndings = new([".png", ".jpg", ".jpeg", ".webp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The most files of one page asked about: a page can list a great many item icons.</summary>
    private const int MaxPageFiles = 200;

    /// <summary>
    /// The pictures a page shows: its main picture (the page image) first, then the biggest of the
    /// others, at most <see cref="MaxPageImages"/>. Icons, drawings (SVG) and anything that is not a
    /// picture stay out. A page that does not exist has none.
    /// </summary>
    public async Task<IReadOnlyList<PageImage>> PageImagesAsync(WikiSite wiki, string title, CancellationToken cancellationToken)
    {
        string? main;
        List<string> files;
        using (var json = await ApiAsync(wiki, $"action=query&titles={Uri.EscapeDataString(title)}&prop=pageimages%7Cimages&piprop=name&imlimit=max&redirects=1", cancellationToken))
        {
            if (!json.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)
                || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0)
            {
                return [];
            }
            var page = pages[0];
            // A name without its namespace: a wiki may call it "Datei:" or "Fichier:" rather than "File:".
            main = page.TryGetProperty("pageimage", out var image) ? image.GetString()?.Replace('_', ' ') : null;
            files = page.TryGetProperty("images", out var images)
                ? [.. images.EnumerateArray().Select(i => i.GetProperty("title").GetString() ?? "").Where(t => t.Length > 0)]
                : [];
        }
        // Photos and drawings only (no SVG logos), the main picture first and icons last, at most so many files.
        files = [.. files
            .Where(f => PictureEndings.Contains(Path.GetExtension(f)))
            .OrderByDescending(f => main is not null && NameOf(f) == main)
            .ThenBy(f => NameOf(f).Contains("icon", StringComparison.OrdinalIgnoreCase))
            .Take(MaxPageFiles)];
        if (files.Count == 0)
        {
            return [];
        }

        // 50 titles at a time, what the API takes; the answer comes in any order.
        var found = new Dictionary<string, PageImage>();
        foreach (var batch in files.Chunk(50))
        {
            var titles = Uri.EscapeDataString(string.Join('|', batch));
            using var json = await ApiAsync(wiki, $"action=query&titles={titles}&prop=imageinfo&iiprop=url%7Csize%7Cmime%7Cextmetadata&iiextmetadatafilter=ImageDescription", cancellationToken);
            if (json.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in pages.EnumerateArray())
                {
                    if (page.TryGetProperty("imageinfo", out var info) && info.ValueKind == JsonValueKind.Array && info.GetArrayLength() > 0
                        && Picture(page.GetProperty("title").GetString() ?? "", info[0], main) is { } picture)
                    {
                        found[picture.FileTitle] = picture;
                    }
                }
            }
        }
        // The main picture, then the biggest: a page lists its item icons alphabetically, often before its real pictures.
        return [.. files.Where(found.ContainsKey).Select(f => found[f]).OrderByDescending(p => p.IsMain).ThenByDescending(p => (long)p.Width * p.Height).Take(MaxPageImages)];
    }

    /// <summary>One file as a picture for the pool; null when it is an icon, a drawing or no picture at all.</summary>
    private static PageImage? Picture(string title, JsonElement info, string? main)
    {
        var mime = info.TryGetProperty("mime", out var m) ? m.GetString() ?? "" : "";
        var width = info.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
        var height = info.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
        if (!PictureTypes.Contains(mime) || Math.Min(width, height) < MinImageSide || !info.TryGetProperty("url", out var url) || url.GetString() is not { } address)
        {
            return null;
        }
        // A file without metadata has "extmetadata": [] (PHP writes an empty object as an array).
        var caption = info.TryGetProperty("extmetadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("ImageDescription", out var description) && description.ValueKind == JsonValueKind.Object
            && description.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? HtmlText.ToText(value.GetString() ?? "").Trim()
            : "";
        return new PageImage(address, title, width, height, caption, main is not null && NameOf(title) == main);
    }

    /// <summary>"File:Karlach portrait.png" → "Karlach portrait.png".</summary>
    private static string NameOf(string title) => title[(title.IndexOf(':', StringComparison.Ordinal) + 1)..];

    private static string PageUrl(WikiSite wiki, string title) =>
        wiki.ArticleUrl.Replace("$1", Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>
    /// Asks each usual API address. Never throws: whatever goes wrong with one address (an error page,
    /// not JSON, a redirect off the sources) only means the API is not there.
    /// </summary>
    private async Task<Discovery> DiscoverAsync(Source source, CancellationToken cancellationToken)
    {
        var settled = true;
        foreach (var path in ApiPaths)
        {
            var api = new Uri($"https://{source.Host}{path}");
            JsonDocument json;
            try
            {
                var page = await web.GetOnSiteAsync(new Uri(api, "?action=query&meta=siteinfo&siprop=general&format=json&formatversion=2"), source, cancellationToken);
                json = JsonDocument.Parse(page.Body);
            }
            catch (WebFailureException ex)
            {
                settled &= !ex.Unreachable;
                continue;
            }
            catch (Exception ex) when (ex is RefusedException or JsonException)
            {
                continue;
            }
            using (json)
            {
                if (SiteInfo(json.RootElement) is { } info)
                {
                    // Fandom answers "server": "https://…", some wikis a protocol-relative "//…".
                    var serverUrl = info.Server.StartsWith("//", StringComparison.Ordinal) ? "https:" + info.Server : info.Server;
                    return new Discovery(new WikiSite(source, api, serverUrl.TrimEnd('/') + info.ArticlePath), Settled: true);
                }
            }
        }
        return new Discovery(null, settled);
    }

    /// <summary>The server and article path from a siteinfo answer; null for any other shape of JSON.</summary>
    private static (string Server, string ArticlePath)? SiteInfo(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Object
        && query.TryGetProperty("general", out var general) && general.ValueKind == JsonValueKind.Object
        && general.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.String
        && general.TryGetProperty("articlepath", out var path) && path.ValueKind == JsonValueKind.String
            ? (server.GetString()!, path.GetString()!)
            : null;

    private async Task<JsonDocument> ApiAsync(WikiSite wiki, string query, CancellationToken cancellationToken)
    {
        var page = await web.GetOnSiteAsync(new Uri(wiki.Api, $"?{query}&format=json&formatversion=2"), wiki.Source, cancellationToken);
        try
        {
            return JsonDocument.Parse(page.Body);
        }
        catch (JsonException)
        {
            throw new WebFailureException($"{wiki.Source.Name} sent an answer that is not the wiki's JSON.");
        }
    }
}
