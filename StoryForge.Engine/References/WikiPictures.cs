using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using StoryForge.Sources;

namespace StoryForge.Engine.References;

/// <summary>
/// Pictures from the project's wikis, through their API: the page that matches a name, the pages
/// the facts came from. A picture is downloaded from the address the wiki itself gives for it
/// (Fandom keeps its pictures on another host), and only over https, only to an address out on the
/// internet (never this machine or the local network, redirects included), and only when it really
/// is a picture.
/// </summary>
/// <param name="http">Sends without following redirects by itself (<see cref="CreateHttp"/>): every hop is checked here.</param>
/// <param name="resolve">Looks up a host's addresses; tests pass their own.</param>
internal sealed class WikiPictures(HttpMessageInvoker http, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IPictureSource
{
    /// <summary>A bigger file is no picture a reference is made from.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    private const int MaxRedirects = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Resolve;

    private readonly Lock _lock = new();

    // The wikis of the last source list asked about: every name and page of a project asks the same
    // ones, and finding a wiki takes a request or two.
    private (string Key, SourceList List, MediaWiki Wikis)? _open;

    /// <summary>
    /// A sender for the wikis and their pictures: no proxy, no redirect followed by itself, and a
    /// connection only to an address that was checked to be out on the internet. The name is looked
    /// up once, here, so it cannot answer one address to the check and another to the connection.
    /// </summary>
    public static HttpMessageInvoker CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, Resolve, cancellationToken),
    });

    private static Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

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
            catch (Exception ex) when (Skippable(ex, cancellationToken))
            {
                // A source that fails or answers oddly finds nothing; the others are still asked.
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
        catch (Exception ex) when (Skippable(ex, cancellationToken))
        {
            Debug.WriteLine($"Pictures on {pageUrl} could not be read: {ex.Message}");
            return [];
        }
    }

    public async Task<DownloadedPicture?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var address = new Uri(url);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!await AllowedAsync(address, timeout.Token))
                {
                    Debug.WriteLine($"{address} is not downloaded: only https addresses out on the internet are.");
                    return null;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("StoryForgeX", SiteWeb.Version));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(references; +https://github.com/Little-God1983/StoryForgeX)"));
                using var response = await http.SendAsync(request, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    address = location.IsAbsoluteUri ? location : new Uri(address, location);
                    continue;
                }
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentLength > MaxBytes)
                {
                    return null;
                }
                var content = await ReadAsync(response.Content, timeout.Token);
                // Said to be a picture, and really one: its own header decides the ending.
                return content is not null && ImageSize.Ending(content) is { } ending ? new DownloadedPicture(content, ending) : null;
            }
            return null;
        }
        catch (Exception ex) when (Skippable(ex, cancellationToken))
        {
            Debug.WriteLine($"{url} could not be downloaded: {ex.Message}");
            return null;
        }
    }

    /// <summary>Connects to the host's addresses when every one of them is public; refuses otherwise.</summary>
    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endPoint.Host.Trim('[', ']'), out var literal) ? [literal] : await resolve(endPoint.Host, cancellationToken);
        if (addresses.Length == 0 || !addresses.All(Public))
        {
            throw new HttpRequestException($"{endPoint.Host} is not out on the internet: nothing is fetched from it.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>https, and every address the host has out on the internet: not this machine, the local network or link-local.</summary>
    private async Task<bool> AllowedAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        var addresses = IPAddress.TryParse(address.IdnHost.Trim('[', ']'), out var literal) ? [literal] : await _resolve(address.IdnHost, cancellationToken);
        return addresses.Length > 0 && addresses.All(Public);
    }

    private static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // An IPv4 address carried inside, which the network may reach: NAT64 (64:ff9b::/96),
            // 6to4 (2002::/16), IPv4-compatible (::a.b.c.d).
            if (b.AsSpan(0, 12).SequenceEqual((byte[])[0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0]) || b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            {
                return PublicV4(b.AsSpan(12, 4));
            }
            if (b[0] == 0x20 && b[1] == 0x02)
            {
                return PublicV4(b.AsSpan(2, 4));
            }
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6UniqueLocal && !address.IsIPv6Multicast;
        }
        return PublicV4(b);
    }

    private static bool PublicV4(ReadOnlySpan<byte> b) =>
        !(b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 198 && b[1] is 18 or 19)
            || b[0] >= 224);

    /// <summary>The body, read up to <see cref="MaxBytes"/>; null when it is bigger.</summary>
    private static async Task<byte[]?> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    /// <summary>What a picture lookup survives: a site that fails, answers oddly, or gives a bad address. Your cancel is not.</summary>
    private static bool Skippable(Exception ex, CancellationToken cancellationToken) =>
        ex is WebFailureException or RefusedException or HttpRequestException or IOException or SocketException
            or UriFormatException or JsonException or InvalidOperationException or KeyNotFoundException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private (SourceList List, MediaWiki Wikis) Open(IReadOnlyList<string> sources)
    {
        var key = string.Join('\n', sources);
        lock (_lock)
        {
            if (_open is not { } open || open.Key != key)
            {
                var list = new SourceList(sources);
                _open = open = (key, list, new MediaWiki(new SiteWeb(http, list)));
            }
            return (open.List, open.Wikis);
        }
    }

    private static async Task<IReadOnlyList<FoundPicture>> PicturesAsync(MediaWiki wikis, WikiSite wiki, string title, string pageUrl, CancellationToken cancellationToken) =>
        [.. (await wikis.PageImagesAsync(wiki, title, cancellationToken)).Select(i => new FoundPicture(i.Url, pageUrl, title, i.Caption, i.Width, i.Height, i.IsMain))];
}
