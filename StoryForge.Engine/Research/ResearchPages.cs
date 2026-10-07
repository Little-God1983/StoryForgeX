using System.Text;
using System.Text.RegularExpressions;

namespace StoryForge.Engine.Research;

/// <summary>
/// The pages the research read in this run, as the research server returned them. A fact may
/// point only to one of these, and its quote must be on it: that is how the engine knows the
/// model did not invent a source or a passage.
/// </summary>
internal sealed partial class ResearchPages
{
    private readonly Dictionary<string, StringBuilder> _pages = [];
    private readonly Dictionary<string, string> _aliases = [];

    public int Count => _pages.Count;

    /// <summary>Adds a part of a page; parts of a long page arrive one fetch at a time.</summary>
    /// <param name="requestedUrl">The URL the model asked for, when it differs from the page's own (a redirect).</param>
    public void Add(string url, string? requestedUrl, string text)
    {
        var key = Key(url);
        if (!_pages.TryGetValue(key, out var page))
        {
            _pages[key] = page = new StringBuilder();
        }
        page.Append('\n').Append(text);
        if (requestedUrl is not null && Key(requestedUrl) != key)
        {
            _aliases[Key(requestedUrl)] = key;
        }
    }

    public void AddAll(ResearchPages other)
    {
        foreach (var (key, text) in other._pages)
        {
            Add(key, null, text.ToString());
        }
        foreach (var (alias, key) in other._aliases)
        {
            _aliases[alias] = key;
        }
    }

    public bool WasRead(string url) => Text(url) is not null;

    /// <summary>
    /// Whether the quote is on the page. Spacing, case, typographic quotes and dashes may differ, and
    /// a quote shortened with "…" counts when every part of it is on the page.
    /// </summary>
    public bool Contains(string url, string quote)
    {
        var page = Text(url);
        if (page is null)
        {
            return false;
        }
        var text = Normalize(page);
        var parts = Ellipsis().Split(quote).Select(Normalize).Where(part => part.Length > 0).ToList();
        return parts.Count > 0 && parts.All(part => text.Contains(part, StringComparison.Ordinal));
    }

    private string? Text(string url)
    {
        var key = Key(url);
        if (_aliases.TryGetValue(key, out var target))
        {
            key = target;
        }
        return _pages.TryGetValue(key, out var page) ? page.ToString() : null;
    }

    /// <summary>
    /// One spelling per page: "https://www.bg3.wiki/wiki/Soul%20Coin/" and "http://bg3.wiki/wiki/Soul_Coin"
    /// are the same page.
    /// </summary>
    internal static string Key(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Replace(' ', '_').TrimEnd('/');
        return host + path + uri.Query;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­')
            {
                continue;   // invisible; a quote copied without them is still the same passage
            }
            builder.Append(c switch
            {
                '‘' or '’' or '‛' or '′' or '`' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                _ => char.ToLowerInvariant(c),
            });
        }
        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\.\.\.|…|\[\.\.\.\]|\[…\]")]
    private static partial Regex Ellipsis();
}
