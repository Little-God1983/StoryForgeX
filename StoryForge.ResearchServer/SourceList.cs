namespace StoryForge.ResearchServer;

/// <summary>
/// One research source: a site, optionally narrowed to a part of it ("reddit.com/r/BaldursGate3").
/// </summary>
/// <param name="Host">Lower case, without "www.".</param>
/// <param name="PathPrefix">"" for the whole site, else "/r/BaldursGate3" (no trailing slash).</param>
internal sealed record Source(string Host, string PathPrefix)
{
    /// <summary>How the source is written in the project's list and in tool calls: "bg3.wiki".</summary>
    public string Name => Host + PathPrefix;

    public Uri Home => new($"https://{Host}{PathPrefix}/");

    public bool Contains(Uri url) =>
        SourceList.HostOf(url) == Host
        && (PathPrefix.Length == 0
            || url.AbsolutePath.Equals(PathPrefix, StringComparison.Ordinal)
            || url.AbsolutePath.StartsWith(PathPrefix + "/", StringComparison.Ordinal));
}

/// <summary>
/// The project's research sources and the one rule the server exists for: a URL can be reached
/// only when it lies on one of them. Plain http(s) on the default port, no user name in the URL.
/// </summary>
internal sealed class SourceList
{
    public SourceList(IEnumerable<string> sources)
    {
        Sources = [.. sources.Select(Parse).OfType<Source>().Distinct()];
    }

    public IReadOnlyList<Source> Sources { get; }

    /// <summary>"bg3.wiki, forgottenrealms.fandom.com" for messages.</summary>
    public string Names => string.Join(", ", Sources.Select(s => s.Name));

    /// <summary>The source <paramref name="url"/> lies on, or null when the server must refuse it.</summary>
    public Source? Find(Uri url) => IsPlainWebAddress(url) ? Sources.FirstOrDefault(s => s.Contains(url)) : null;

    /// <summary>Whether <paramref name="url"/> is on the same site as <paramref name="source"/>, whatever its path.</summary>
    public bool IsOnSite(Uri url, Source source) => IsPlainWebAddress(url) && Sources.Contains(source) && HostOf(url) == source.Host;

    /// <summary>The source a tool call names, written as in the list or as a URL ("https://bg3.wiki/").</summary>
    public Source? FindByName(string name)
    {
        var wanted = Parse(name);
        return wanted is null ? null : Sources.FirstOrDefault(s => s == wanted);
    }

    /// <summary>Reads "bg3.wiki", "https://www.bg3.wiki/" or "reddit.com/r/BaldursGate3/"; null if it names no site.</summary>
    public static Source? Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || !IsPlainWebAddress(url) || !url.Host.Contains('.'))
        {
            return null;
        }
        return new Source(HostOf(url), url.AbsolutePath.TrimEnd('/'));
    }

    internal static string HostOf(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static bool IsPlainWebAddress(Uri url) =>
        url.Scheme is "http" or "https" && url.IsDefaultPort && url.UserInfo.Length == 0 && url.HostNameType == UriHostNameType.Dns;
}
