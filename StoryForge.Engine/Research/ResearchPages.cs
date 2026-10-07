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
    private readonly Dictionary<string, Page> _pages = [];
    private readonly Dictionary<string, string> _aliases = [];

    public int Count => _pages.Count;

    /// <summary>Adds a part of a page; parts of a long page arrive one fetch at a time, in any order.</summary>
    /// <param name="requestedUrl">The URL the model asked for, when it differs from the page's own (a redirect).</param>
    /// <param name="offset">Where the part starts in the page text; null when the answer did not say.</param>
    public void Add(string url, string? requestedUrl, string text, int? offset = null)
    {
        var key = Key(url);
        if (!_pages.TryGetValue(key, out var page))
        {
            _pages[key] = page = new Page();
        }
        page.Add(text, offset);
        if (requestedUrl is not null && Key(requestedUrl) != key)
        {
            _aliases[Key(requestedUrl)] = key;
        }
    }

    public void AddAll(ResearchPages other)
    {
        foreach (var (key, page) in other._pages)
        {
            foreach (var (offset, text) in page.Parts)
            {
                Add(key, null, text, offset);
            }
            foreach (var text in page.Loose)
            {
                Add(key, null, text);
            }
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
        return _pages.TryGetValue(key, out var page) ? page.Text() : null;
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

    /// <summary>One page's text, put together from the parts the research read.</summary>
    private sealed class Page
    {
        /// <summary>Parts by where they start in the page text.</summary>
        public SortedDictionary<int, string> Parts { get; } = [];

        /// <summary>Parts whose place the answer did not give; they never join a neighbour.</summary>
        public List<string> Loose { get; } = [];

        public void Add(string text, int? offset)
        {
            if (offset is not { } at)
            {
                Loose.Add(text);
            }
            else if (!Parts.TryGetValue(at, out var known) || known.Length < text.Length)
            {
                Parts[at] = text;
            }
        }

        /// <summary>
        /// Parts that meet are joined as they are, so a cut in the middle of a word disappears; an
        /// overlap is not repeated; a gap becomes a line break, never a join that is not on the page.
        /// </summary>
        public string Text()
        {
            var text = new StringBuilder();
            var end = -1;
            foreach (var (offset, part) in Parts)
            {
                if (end >= 0 && offset < end)
                {
                    if (offset + part.Length > end)
                    {
                        text.Append(part, end - offset, offset + part.Length - end);
                        end = offset + part.Length;
                    }
                    continue;
                }
                if (end >= 0 && offset > end)
                {
                    text.Append('\n');
                }
                text.Append(part);
                end = offset + part.Length;
            }
            foreach (var part in Loose)
            {
                text.Append('\n').Append(part);
            }
            return text.ToString();
        }
    }
}
