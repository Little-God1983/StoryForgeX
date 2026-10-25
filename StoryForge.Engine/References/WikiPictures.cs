using System.Diagnostics;
using StoryForge.Sources;

namespace StoryForge.Engine.References;

/// <summary>
/// Pictures from the project's wikis, through their API: the page that matches a name, the pages
/// the facts came from. A picture is downloaded from the address the wiki itself gives for it
/// (Fandom keeps its pictures on another host), and only when it really is a picture.
/// </summary>
internal sealed class WikiPictures(HttpMessageHandler handler) : IPictureSource
{
    /// <summary>A bigger file is no picture a reference is made from.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> Endings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
    };

    public async Task<IReadOnlyList<FoundPicture>> SearchAsync(IReadOnlyList<string> sources, string name, CancellationToken cancellationToken)
    {
        var (list, wikis) = Open(sources);
        foreach (var source in list.Sources)
        {
            try
            {
                if (await wikis.FindAsync(source, cancellationToken) is not { } wiki)
                {
                    continue;
                }
                var hits = await wikis.SearchAsync(wiki, name, cancellationToken);
                // The page called so, else the best hit: "Karlach" before "Karlach (origin)".
                var page = hits.FirstOrDefault(h => ReferenceList.Same(h.Title, name)) ?? hits.FirstOrDefault();
                if (page is null)
                {
                    continue;
                }
                var found = await PicturesAsync(wikis, wiki, page.Title, page.Url, cancellationToken);
                if (found.Count > 0)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is WebFailureException or RefusedException)
            {
                Debug.WriteLine($"Looking for pictures of {name} on {source.Name} failed: {ex.Message}");
            }
        }
        return [];
    }

    public async Task<IReadOnlyList<FoundPicture>> PageAsync(IReadOnlyList<string> sources, string pageUrl, CancellationToken cancellationToken)
    {
        var (list, wikis) = Open(sources);
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var url) || list.Find(url) is not { } source)
        {
            return [];
        }
        try
        {
            return await wikis.FindAsync(source, cancellationToken) is { } wiki && MediaWiki.TitleOf(wiki, url) is { } title
                ? await PicturesAsync(wikis, wiki, title, pageUrl, cancellationToken)
                : [];
        }
        catch (Exception ex) when (ex is WebFailureException or RefusedException)
        {
            Debug.WriteLine($"Pictures on {pageUrl} could not be read: {ex.Message}");
            return [];
        }
    }

    public async Task<DownloadedPicture?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!response.IsSuccessStatusCode || !Endings.TryGetValue(type, out var ending) || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return content.Length is > 0 and <= MaxBytes ? new DownloadedPicture(content, ending) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine($"{url} could not be downloaded: {ex.Message}");
            return null;
        }
    }

    private (SourceList List, MediaWiki Wikis) Open(IReadOnlyList<string> sources)
    {
        var list = new SourceList(sources);
        return (list, new MediaWiki(new SiteWeb(new HttpMessageInvoker(handler, disposeHandler: false), list)));
    }

    private static async Task<IReadOnlyList<FoundPicture>> PicturesAsync(MediaWiki wikis, WikiSite wiki, string title, string pageUrl, CancellationToken cancellationToken) =>
        [.. (await wikis.PageImagesAsync(wiki, title, cancellationToken)).Select(i => new FoundPicture(i.Url, pageUrl, title, i.Caption, i.Width, i.Height, i.IsMain))];
}
