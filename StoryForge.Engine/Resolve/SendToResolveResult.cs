using System.Text;
using System.Text.Json;

namespace StoryForge.Engine.Resolve;

/// <summary>
/// What the script reports. <c>Stage</c> names how far it got — "connect", "create", "rate",
/// "import", "markers", "open", "done" — so a failure can say which step failed rather than that
/// something did.
/// </summary>
internal sealed record SendToResolveResult(
    bool Ok, string Stage, string? Problem, string? TimelineName, int VideoItems, int MarkersPlaced)
{
    /// <summary>How many of Resolve's Software Update windows the sender closed while the script
    /// ran. Set by <see cref="ResolveSender"/>, not by the script, so <see cref="Parse"/> leaves it 0.</summary>
    public int UpdateDialogsClosed { get; init; }

    /// <summary>
    /// Reads the script's output. Every path returns a result; none throws. Its stdout is treated as
    /// untrusted — a traceback, an empty string and Resolve's own startup chatter all have to end up
    /// as a sentence the user can read.
    /// </summary>
    public static SendToResolveResult Parse(int exitCode, string stdout, string stderr)
    {
        if (LastJsonObject(stdout) is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                return new SendToResolveResult(
                    Ok: Bool(root, "ok"),
                    Stage: String(root, "stage") ?? "unknown",
                    Problem: String(root, "problem"),
                    TimelineName: String(root, "timelineName"),
                    VideoItems: Int(root, "videoItems"),
                    MarkersPlaced: Int(root, "markersPlaced"));
            }
            catch (JsonException)
            {
                // Falls through to the unparseable path below: something that looked like an object
                // was not one, which is exactly the case this method exists to survive.
            }
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var said = string.IsNullOrWhiteSpace(detail) ? "it printed nothing" : detail.Trim();

        return new SendToResolveResult(false, "unknown",
            $"The Resolve script exited with code {exitCode} and {said}", null, 0, 0);
    }

    /// <summary>The LAST top-level object on stdout THAT CARRIES AN "ok" PROPERTY, falling back to
    /// the last object of any shape only if none does — because some machines print interpreter
    /// noise ahead of the script's own object, or after it: Resolve's own shutdown chatter, a
    /// fusion script's own JSON, anything else printed once the script's single object is already
    /// out. Preferring the plain last object over one that actually looks like this script's own
    /// output would let that trailing noise silently replace a real result — a send that created
    /// the project, imported the timeline and placed every marker reported as "failed at 'unknown'"
    /// because something printed <c>{"status": "ok"}</c> on the way out. Scans left to right for a {
    /// that opens a complete JSON object, using <see cref="JsonDocument.TryParseValue"/> — which,
    /// unlike <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>, stops reading the
    /// instant the object closes rather than rejecting any content left after it, so trailing bytes
    /// are never a reason to fall through to the "exited with code N and …" path below.
    ///
    /// <para>Left to right, not right to left: once a candidate parses as a complete object, the
    /// search resumes AFTER it, so a later, separate top-level object (if the script somehow printed
    /// more than one) wins as "last", while braces inside THIS object's own string values — already
    /// consumed as part of it — are never revisited as false starts. A right-to-left walk that
    /// tolerates trailing content cannot make that distinction: the first "{ }" it meets while
    /// backing up through a string like <c>"malformed JSON: { } in input"</c> parses as a complete,
    /// valid, entirely wrong empty object.</para></summary>
    private static string? LastJsonObject(string stdout)
    {
        var trimmed = stdout.Trim();
        string? last = null;
        string? lastWithOk = null;
        var searchFrom = 0;

        while (true)
        {
            var pos = trimmed.IndexOf('{', searchFrom);
            if (pos < 0)
                break;

            var bytes = Encoding.UTF8.GetBytes(trimmed[pos..]);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            try
            {
                if (JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    using (doc)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            var candidate = Encoding.UTF8.GetString(bytes, 0, (int)reader.BytesConsumed);
                            last = candidate;
                            if (doc.RootElement.TryGetProperty("ok", out _))
                                lastWithOk = candidate;
                            searchFrom = pos + candidate.Length;
                            continue;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not a valid value starting here — try the very next character.
            }

            searchFrom = pos + 1;
        }

        return lastWithOk ?? last;
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
