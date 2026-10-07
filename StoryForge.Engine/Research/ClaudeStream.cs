using System.Text.Json;
using StoryForge.Client;

namespace StoryForge.Engine.Research;

/// <summary>Text the research server puts into its answers; StoryForge.ResearchServer has the same (pinned by a test).</summary>
internal static class ServerAnswers
{
    public const string RefusedPrefix = "Refused:";
    public const string PagePrefix = "Page: ";
    public const string TextMarker = "-----";
}

/// <summary>
/// Reads Claude CLI's stream-json output as it comes: every search and page read becomes a line of
/// activity, every page read is kept for checking quotes, and the last line carries the answer.
/// </summary>
internal sealed class ClaudeStream(ResearchPages pages, IProgress<ActivityLine> activity, TimeProvider clock)
{
    public const string SearchTool = "mcp__storyforge__search";
    public const string FetchTool = "mcp__storyforge__fetch";
    private const string AnswerTool = "StructuredOutput";

    private readonly Dictionary<string, (string Tool, JsonElement Input)> _calls = [];

    public string? Session { get; private set; }

    /// <summary>The structured answer, if the model gave one.</summary>
    public JsonElement? Output { get; private set; }

    /// <summary>Why the run ended without an answer, as Claude CLI says it; null if it did not say.</summary>
    public string? Error { get; private set; }

    /// <summary>The research server's state when it did not connect, e.g. "failed".</summary>
    public string? ServerProblem { get; private set; }

    public decimal? CostUsd { get; private set; }

    public void Read(string line)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;   // not one of the stream's lines (a warning, say)
        }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            switch (Text(root, "type"))
            {
                case "system" when Text(root, "subtype") == "init":
                    Session = Text(root, "session_id") ?? Session;
                    ReadServers(root);
                    break;
                case "assistant":
                    foreach (var block in Blocks(root).Where(b => Text(b, "type") == "tool_use"))
                    {
                        var name = Text(block, "name") ?? "";
                        _calls[Text(block, "id") ?? ""] = (name, block.TryGetProperty("input", out var input) ? input.Clone() : default);
                        if (name == AnswerTool)
                        {
                            Report(ActivityKind.Model, "writing the fact sheet");
                        }
                    }
                    break;
                case "user":
                    foreach (var block in Blocks(root).Where(b => Text(b, "type") == "tool_result"))
                    {
                        if (_calls.Remove(Text(block, "tool_use_id") ?? "", out var call))
                        {
                            OnResult(call.Tool, call.Input, block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True, ResultText(block));
                        }
                    }
                    break;
                case "result":
                    Session = Text(root, "session_id") ?? Session;
                    if (root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var usd))
                    {
                        CostUsd = usd;
                    }
                    if (root.TryGetProperty("structured_output", out var output) && output.ValueKind == JsonValueKind.Object)
                    {
                        Output = output.Clone();
                    }
                    else if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    {
                        Error = Text(root, "result") ?? Text(root, "subtype") ?? "unknown error";
                    }
                    break;
            }
        }
    }

    private void ReadServers(JsonElement init)
    {
        if (!init.TryGetProperty("mcp_servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var ours = servers.EnumerateArray().FirstOrDefault(s => Text(s, "name") == ClaudeCliResearchAgent.ServerName);
        var status = ours.ValueKind == JsonValueKind.Object ? Text(ours, "status") : "not loaded";
        ServerProblem = status == "connected" ? null : status;
    }

    private void OnResult(string tool, JsonElement input, bool isError, string text)
    {
        var refused = isError && text.StartsWith(ServerAnswers.RefusedPrefix, StringComparison.Ordinal);
        switch (tool)
        {
            case SearchTool:
                var search = $"{Text(input, "source")}  \"{Text(input, "query")}\"";
                if (refused)
                {
                    Report(ActivityKind.Refused, $"{search}  (not a project source)");
                }
                else if (isError)
                {
                    Report(ActivityKind.Failed, $"{search}  – {FirstLine(text)}");
                }
                else
                {
                    Report(ActivityKind.Search, $"{search}  → {Hits(text)}");
                }
                break;
            case FetchTool:
                var requested = Text(input, "url") ?? "";
                if (refused)
                {
                    Report(ActivityKind.Refused, $"{Display(requested)}  (not a project source)");
                }
                else if (isError)
                {
                    Report(ActivityKind.Failed, $"{Display(requested)}  – {FirstLine(text)}");
                }
                else
                {
                    var (url, body) = Page(text, requested);
                    pages.Add(url, requested, body);
                    Report(ActivityKind.Fetch, Display(url));
                }
                break;
        }
    }

    /// <summary>The page's own URL and its text, from "Page: …\n…\n-----\n&lt;text&gt;".</summary>
    private static (string Url, string Text) Page(string answer, string requested)
    {
        var lines = answer.Split('\n');
        var url = lines.Length > 0 && lines[0].StartsWith(ServerAnswers.PagePrefix, StringComparison.Ordinal)
            ? lines[0][ServerAnswers.PagePrefix.Length..].Trim()
            : requested;
        var marker = Array.IndexOf(lines, ServerAnswers.TextMarker);
        return (url, marker < 0 ? answer : string.Join('\n', lines[(marker + 1)..]));
    }

    private static string Hits(string answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer);
            var count = json.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                ? results.GetArrayLength()
                : 0;
            return count == 1 ? "1 hit" : $"{count} hits";
        }
        catch (JsonException)
        {
            return "results";
        }
    }

    private void Report(ActivityKind kind, string text) => activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>"bg3.wiki/wiki/Soul_Coins:_A_Treatise": without the scheme, escapes undone.</summary>
    private static string Display(string url) => Uri.UnescapeDataString(
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..]
        : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? url[7..]
        : url);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static IEnumerable<JsonElement> Blocks(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().ToList()
            : [];

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return "";
        }
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(part => Text(part, "text") ?? "")),
            _ => "",
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
