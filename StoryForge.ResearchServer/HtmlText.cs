using System.Net;
using System.Text.RegularExpressions;

namespace StoryForge.ResearchServer;

/// <summary>
/// Turns a page into the text a reader sees: no markup, scripts, menus or edit links; one line per
/// paragraph. Facts quote this text, and the engine checks each quote against it.
/// </summary>
internal static partial class HtmlText
{
    public static string ToText(string html)
    {
        var text = html;
        text = Hidden().Replace(text, " ");
        // The main content where a page marks it; menus and footers are noise for research.
        var main = MainPart().Match(text);
        if (main.Success)
        {
            text = main.Groups["body"].Value;
        }
        text = WikiChrome().Replace(text, " ");
        // Line breaks in the markup are only spacing; the tags decide where a line ends.
        text = SourceBreaks().Replace(text, " ");
        text = LineBreaks().Replace(text, "\n");
        text = CellBreaks().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Invisible().Replace(text, "");
        text = Spaces().Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<!--.*?-->|<(script|style|noscript|svg|head|template|nav|footer)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<(main|article)\b[^>]*>(?<body>.*)</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainPart();

    // "[edit]" links and footnote markers ("[3]") that MediaWiki puts into the running text.
    [GeneratedRegex(@"<span\b[^>]*class=""mw-editsection[^""]*""[^>]*>.*?</span>\s*</span>|<sup\b[^>]*class=""[^""]*reference[^""]*""[^>]*>.*?</sup>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex WikiChrome();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/table|/section|/ul|/ol|/dl|/dd|/dt|/blockquote|/caption|/figcaption|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex SourceBreaks();

    [GeneratedRegex(@"<\s*/(td|th)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellBreaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    // Zero-width spaces, word joiners and byte order marks: invisible, but they would break a quote apart.
    [GeneratedRegex(@"[​-‍⁠﻿­]")]
    private static partial Regex Invisible();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
