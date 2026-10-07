using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StoryForge.ResearchServer;

/// <summary>Text the engine reads back out of the tool results; StoryForge.Engine keeps a copy (pinned by a test).</summary>
internal static class Answers
{
    /// <summary>Starts every answer to a URL that is not on the source list.</summary>
    public const string RefusedPrefix = "Refused:";

    /// <summary>Starts the first line of a fetch answer: "Page: https://bg3.wiki/wiki/Soul_Coin".</summary>
    public const string PagePrefix = "Page: ";

    /// <summary>The line after the header; the page text follows it.</summary>
    public const string TextMarker = "-----";
}

/// <summary>
/// The two tools Claude gets for the Research stage. Neither reaches anything that is not on the
/// project's source list; every refusal says which sites are allowed.
/// </summary>
[McpServerToolType]
internal sealed class ResearchTools(SourceList sources, SiteWeb web, MediaWiki wikis)
{
    /// <summary>How much page text one fetch returns; longer pages continue with a later start.</summary>
    public const int PageChunk = 12_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "search", ReadOnly = true, OpenWorld = true)]
    [Description("Searches one of the project's sources and returns page titles, URLs and snippets. Only the sources the prompt lists can be searched.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("The source to search, exactly as listed, e.g. \"bg3.wiki\".")] string source,
        [Description("What to look for, e.g. \"soul coin\".")] string query,
        CancellationToken cancellationToken)
    {
        var site = sources.FindByName(source);
        if (site is null)
        {
            return Error($"{Answers.RefusedPrefix} \"{source}\" is not one of the project's sources ({sources.Names}).");
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("The query is empty.");
        }
        try
        {
            var wiki = await wikis.FindAsync(site, cancellationToken);
            if (wiki is null)
            {
                return Error($"{site.Name} has no search the research tools can use. Fetch its pages instead, starting with {site.Home}");
            }
            var hits = await wikis.SearchAsync(wiki, query.Trim(), cancellationToken);
            return Ok(JsonSerializer.Serialize(new { source = site.Name, query = query.Trim(), results = hits }, Json));
        }
        catch (Exception ex) when (ex is RefusedException or WebFailureException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "fetch", ReadOnly = true, OpenWorld = true)]
    [Description("Reads a page on one of the project's sources and returns its text. Long pages come in parts: call again with the start the answer names.")]
    public async Task<CallToolResult> FetchAsync(
        [Description("The page's full URL, e.g. \"https://bg3.wiki/wiki/Soul_Coin\".")] string url,
        [Description("Where in the page text to start; 0 for the beginning.")] int start = 0,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var address))
        {
            return Error($"\"{url}\" is not a full URL. Write it with https://, e.g. {sources.Sources.FirstOrDefault()?.Home}");
        }
        var site = sources.Find(address);
        if (site is null)
        {
            return Error(new RefusedException(address, sources.Names).Message);
        }
        try
        {
            var (finalUrl, title, text) = await ReadAsync(site, address, cancellationToken);
            return Ok(Format(finalUrl, title, text, Math.Max(0, start)));
        }
        catch (Exception ex) when (ex is RefusedException or WebFailureException)
        {
            return Error(ex.Message);
        }
    }

    private async Task<(string Url, string? Title, string Text)> ReadAsync(Source site, Uri address, CancellationToken cancellationToken)
    {
        // A wiki page is read through the API: clean text, and its redirects stay on the wiki.
        var wiki = await wikis.FindAsync(site, cancellationToken);
        if (wiki is not null && MediaWiki.TitleOf(wiki, address) is { } wikiTitle)
        {
            var page = await wikis.PageAsync(wiki, wikiTitle, cancellationToken);
            if (sources.Find(new Uri(page.Url)) is null)
            {
                throw new RefusedException(new Uri(page.Url), sources.Names);
            }
            return (page.Url, page.Title, page.Text);
        }
        var response = await web.GetAsync(address, cancellationToken);
        var isHtml = response.MediaType is null || response.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
        return (response.Url.ToString(), null, isHtml ? HtmlText.ToText(response.Body) : response.Body);
    }

    /// <summary>
    /// "Page: …", the title, which part of the text this is, then the text. The engine reads the URL
    /// and the text back to check that every quote in the fact sheet is really on its page.
    /// </summary>
    internal static string Format(string url, string? title, string text, int start)
    {
        var from = Math.Min(start, text.Length);
        var to = Math.Min(text.Length, from + PageChunk);
        var answer = new StringBuilder();
        answer.Append(Answers.PagePrefix).Append(url).Append('\n');
        if (title is not null)
        {
            answer.Append("Title: ").Append(title).Append('\n');
        }
        answer.Append($"Characters {from} to {to} of {text.Length}.");
        if (to < text.Length)
        {
            answer.Append($" The page continues: fetch it again with start={to}.");
        }
        answer.Append('\n').Append(Answers.TextMarker).Append('\n').Append(text, from, to - from);
        return answer.ToString();
    }

    private static CallToolResult Ok(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = false };

    private static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };
}
