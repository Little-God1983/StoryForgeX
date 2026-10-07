using System.Collections.Concurrent;
using System.Text.Json;

namespace StoryForge.ResearchServer;

/// <summary>A source that runs MediaWiki (bg3.wiki, every Fandom wiki): where its API is and how its page URLs look.</summary>
/// <param name="ArticleUrl">"https://bg3.wiki/wiki/$1": a page title goes where $1 is.</param>
internal sealed record WikiSite(Source Source, Uri Api, string ArticleUrl);

internal sealed record SearchHit(string Title, string Url, string Snippet);

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

    private readonly ConcurrentDictionary<Source, Task<WikiSite?>> _sites = new();

    /// <summary>The wiki behind <paramref name="source"/>, or null when it is not a MediaWiki site.</summary>
    public async Task<WikiSite?> FindAsync(Source source, CancellationToken cancellationToken)
    {
        // Shared by every call, so no single call's token; each request has its own timeout.
        var found = await _sites.GetOrAdd(source, s => DiscoverAsync(s, CancellationToken.None)).WaitAsync(cancellationToken);
        if (found is null)
        {
            // Maybe the site was only down for a moment: the next call asks again.
            _sites.TryRemove(source, out _);
        }
        return found;
    }

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

    private static string PageUrl(WikiSite wiki, string title) =>
        wiki.ArticleUrl.Replace("$1", Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/", StringComparison.Ordinal), StringComparison.Ordinal);

    private async Task<WikiSite?> DiscoverAsync(Source source, CancellationToken cancellationToken)
    {
        foreach (var path in ApiPaths)
        {
            var api = new Uri($"https://{source.Host}{path}");
            JsonDocument json;
            try
            {
                var page = await web.GetOnSiteAsync(new Uri(api, "?action=query&meta=siteinfo&siprop=general&format=json&formatversion=2"), source, cancellationToken);
                json = JsonDocument.Parse(page.Body);
            }
            catch (Exception ex) when (ex is WebFailureException or JsonException)
            {
                continue;
            }
            using (json)
            {
                if (json.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("general", out var general)
                    && general.TryGetProperty("server", out var server) && general.TryGetProperty("articlepath", out var articlePath))
                {
                    // Fandom answers "server": "https://…", some wikis a protocol-relative "//…".
                    var serverUrl = server.GetString() ?? "";
                    if (serverUrl.StartsWith("//", StringComparison.Ordinal))
                    {
                        serverUrl = "https:" + serverUrl;
                    }
                    return new WikiSite(source, api, serverUrl.TrimEnd('/') + articlePath.GetString());
                }
            }
        }
        return null;
    }

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
