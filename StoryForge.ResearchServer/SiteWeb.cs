using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace StoryForge.ResearchServer;

/// <summary>A URL the server will not reach because it is not on the project's source list.</summary>
internal sealed class RefusedException(Uri url, string sources)
    : Exception($"{Answers.RefusedPrefix} {url} is not on the project's source list ({sources}). Only those sites can be reached.");

/// <summary>A page that could not be read: an HTTP error, a timeout, too many redirects.</summary>
internal sealed class WebFailureException(string message) : Exception(message);

internal sealed record WebPage(Uri Url, string? MediaType, string Body);

/// <summary>
/// Every request the server makes goes through here, and every one is checked against the source
/// list first, including each redirect: a source page that redirects elsewhere is refused there.
/// </summary>
internal sealed class SiteWeb(HttpMessageInvoker http, SourceList sources)
{
    private const int MaxRedirects = 5;
    private const int MaxBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static HttpMessageInvoker CreateHttp() => new(new SocketsHttpHandler
    {
        // Redirects are followed by hand, so each hop can be checked.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    /// <summary>Reads a page a tool call asked for: the URL and every redirect must lie on a source.</summary>
    public Task<WebPage> GetAsync(Uri url, CancellationToken cancellationToken) =>
        GetAsync(url, next => sources.Find(next) is not null, cancellationToken);

    /// <summary>
    /// Reads a page on <paramref name="site"/>'s host, outside its path if need be: a source narrowed to
    /// part of a wiki still needs the wiki's API, which sits on the same site.
    /// </summary>
    public Task<WebPage> GetOnSiteAsync(Uri url, Source site, CancellationToken cancellationToken) =>
        GetAsync(url, next => sources.IsOnSite(next, site), cancellationToken);

    private async Task<WebPage> GetAsync(Uri url, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!allowed(url))
            {
                throw new RefusedException(url, sources.Names);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("StoryForgeX", Version));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(research; +https://github.com/Little-God1983/StoryForgeX)"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebFailureException($"{url} did not answer within {Timeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new WebFailureException($"{url} could not be reached: {ex.Message}");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new WebFailureException($"HTTP {(int)response.StatusCode} from {url}.");
                }
                return new WebPage(url, response.Content.Headers.ContentType?.MediaType, await ReadAsync(response, deadline.Token));
            }
        }
        throw new WebFailureException($"{url} redirects more than {MaxRedirects} times.");
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length < MaxBytes)
        {
            buffer.Write(chunk, 0, Math.Min(read, MaxBytes - (int)buffer.Length));
        }
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
        }
        catch (ArgumentException)
        {
            // An unknown charset label: UTF-8 is what almost every site sends anyway.
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static readonly string Version =
        typeof(SiteWeb).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
}
