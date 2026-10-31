// Taken over from RawCutX's tests with its Send to Resolve (#71).
using StoryForge.Engine.Resolve;

namespace StoryForge.Engine.Tests.Resolve;

/// <summary>
/// The two ends of the one script call: what the app hands the script, and what it makes of what
/// comes back. Parsing is defensive on purpose — the script is the only part of this feature with no
/// unit tests of its own, so the C# side must survive it printing anything at all.
/// </summary>
public sealed class SendToResolveResultTests
{
    private static SendToResolveRequest ARequest(params ResolveMarker[] markers) =>
        new("My Project", @"D:\clips\timeline.fcpxml", "60", PlaceMarkers: true, markers);

    [Fact]
    public void TheRequestCarriesEveryMarkerFieldTheScriptNeeds()
    {
        var json = ARequest(new ResolveMarker(45, "Yellow", "mid edits: 8 breath lifts")).ToJson();

        Assert.Contains("\"projectName\":\"My Project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frameRate\":\"60\"", json, StringComparison.Ordinal);
        Assert.Contains("\"placeMarkers\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"frame\":45", json, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"Yellow\"", json, StringComparison.Ordinal);
        Assert.Contains("mid edits: 8 breath lifts", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(60, 1, "60")]
    [InlineData(30, 1, "30")]
    [InlineData(30000, 1001, "29.97")]
    [InlineData(24000, 1001, "23.976")]
    public void FrameRateText_IsWhatResolveAccepts(int num, int den, string expected)
    {
        // Resolve takes timelineFrameRate as a string. A whole rate must not arrive as "60.00" and a
        // fractional one must keep enough places to be the rate it means.
        Assert.Equal(expected, SendToResolveRequest.FrameRateText(num, den));
    }

    [Fact]
    public void ASuccessfulRun_Parses()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"Software-30","videoItems":165,"markersPlaced":17}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal("Software-30", result.TimelineName);
        Assert.Equal(165, result.VideoItems);
        Assert.Equal(17, result.MarkersPlaced);
    }

    [Fact]
    public void AFailedStage_KeepsTheStageAndTheProblem()
    {
        var json = """
        {"ok":false,"stage":"create","problem":"a project called 'My Project' already exists","timelineName":null,"videoItems":0,"markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(1, json, "");

        Assert.False(result.Ok);
        Assert.Equal("create", result.Stage);
        Assert.Contains("already exists", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputThatIsNotJson_BecomesAReportedFailure_NotAnException()
    {
        var result = SendToResolveResult.Parse(1, "Traceback (most recent call last):", "boom");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.Contains("boom", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOutputWithANonZeroExit_StillSaysSomethingUseful()
    {
        var result = SendToResolveResult.Parse(9, "", "");

        Assert.False(result.Ok);
        Assert.Contains("9", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonPrintedAfterOtherChatter_IsStillFound()
    {
        // Resolve's interpreter prints its own noise on startup on some machines. The last JSON
        // object on stdout is the result; anything before it is not ours.
        var result = SendToResolveResult.Parse(0,
            "some startup noise\n{\"ok\":true,\"stage\":\"done\",\"problem\":null,\"timelineName\":\"T\",\"videoItems\":3,\"markersPlaced\":0}",
            "");

        Assert.True(result.Ok);
        Assert.Equal("T", result.TimelineName);
    }

    /// <summary>
    /// Review finding 4: <c>JsonDocument.Parse</c> rejects any content after the root value, so a
    /// byte printed AFTER the script's own object — a stray print, an interpreter shutdown message —
    /// turned a good result into "exited with code 0 and {the whole blob}". The chatter-BEFORE case
    /// above was already handled by walking back to the last <c>{</c>; this is the same problem from
    /// the other side.
    /// </summary>
    [Fact]
    public void JsonFollowedByTrailingChatter_IsStillParsed()
    {
        var result = SendToResolveResult.Parse(0,
            "{\"ok\":true,\"stage\":\"done\",\"problem\":null,\"timelineName\":\"T\",\"videoItems\":3,\"markersPlaced\":0}\nsome shutdown noise",
            "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal("T", result.TimelineName);
        Assert.Equal(3, result.VideoItems);
    }

    /// <summary>
    /// Review finding: preferring the plain LAST top-level object let anything printed after the
    /// script's own object silently replace it — an interpreter shutdown message, a Resolve plugin,
    /// a fusion script's own JSON. Here that trailing object lacks "ok" entirely, the shape none of
    /// this script's own output has: the fix keeps the left-to-right walk but prefers the last object
    /// that actually carries an "ok" property, falling back to the plain last object only when none
    /// does. Without the fix this parses as <c>Ok=false, Stage="unknown"</c> on a send that actually
    /// created the project, imported the timeline and placed every marker.
    /// </summary>
    [Fact]
    public void TrailingJsonWithNoOkProperty_DoesNotOverrideTheRealResult()
    {
        var result = SendToResolveResult.Parse(0,
            "{\"ok\":true,\"stage\":\"done\",\"problem\":null,\"timelineName\":\"T\",\"videoItems\":5,\"markersPlaced\":3}\n" +
            "{\"status\":\"ok\"}",
            "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal("T", result.TimelineName);
        Assert.Equal(5, result.VideoItems);
        Assert.Equal(3, result.MarkersPlaced);
    }

    [Fact]
    public void JsonSurroundedByChatterOnBothSides_IsStillParsed()
    {
        var result = SendToResolveResult.Parse(0,
            "startup noise\n{\"ok\":true,\"stage\":\"done\",\"problem\":null,\"timelineName\":\"T\",\"videoItems\":3,\"markersPlaced\":0}\ntrailing noise",
            "");

        Assert.True(result.Ok);
        Assert.Equal("T", result.TimelineName);
    }

    // Regression tests for Critical Bug #1: Int() must guard ValueKind before calling TryGetInt32
    // to avoid InvalidOperationException on non-number JSON values.

    [Fact]
    public void VideoItemsFieldIsString_DoesNotThrow()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"T","videoItems":"165","markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal(0, result.VideoItems); // Defaults to 0 instead of throwing
    }

    [Fact]
    public void VideoItemsFieldIsNull_DoesNotThrow()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"T","videoItems":null,"markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal(0, result.VideoItems); // Defaults to 0 instead of throwing
    }

    [Fact]
    public void MarkersPlacedFieldIsString_DoesNotThrow()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"T","videoItems":5,"markersPlaced":"42"}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal(0, result.MarkersPlaced); // Defaults to 0 instead of throwing
    }

    [Fact]
    public void MarkersPlacedFieldIsNull_DoesNotThrow()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"T","videoItems":5,"markersPlaced":null}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal(0, result.MarkersPlaced); // Defaults to 0 instead of throwing
    }

    // Regression tests for Critical Bug #2: LastJsonObject must use JsonDocument to understand
    // string escaping, not naive brace counting. Otherwise braces inside string values corrupt parsing.

    [Fact]
    public void LeftBraceInsideTimelineName_ParsesCorrectly()
    {
        var json = """
        {"ok":true,"stage":"done","problem":null,"timelineName":"Take {1","videoItems":5,"markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.True(result.Ok);
        Assert.Equal("done", result.Stage);
        Assert.Equal("Take {1", result.TimelineName); // Correctly extracts the full name with brace
    }

    [Fact]
    public void RightBraceInsideProblem_ParsesCorrectly()
    {
        var json = """
        {"ok":false,"stage":"import","problem":"unexpected } token","timelineName":null,"videoItems":0,"markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(1, json, "");

        Assert.False(result.Ok);
        Assert.Equal("import", result.Stage);
        Assert.Equal("unexpected } token", result.Problem); // Correctly extracts the full error message
    }

    [Fact]
    public void BothBracesInsideProblem_ParsesCorrectly()
    {
        var json = """
        {"ok":false,"stage":"markers","problem":"malformed JSON: { } in input","timelineName":null,"videoItems":0,"markersPlaced":0}
        """;

        var result = SendToResolveResult.Parse(1, json, "");

        Assert.False(result.Ok);
        Assert.Equal("markers", result.Stage);
        Assert.Equal("malformed JSON: { } in input", result.Problem);
    }

    // Additional adversarial tests to ensure Parse never throws for any input.

    [Fact]
    public void EmptyString_DoesNotThrow()
    {
        var result = SendToResolveResult.Parse(1, "", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void WhitespaceOnly_DoesNotThrow()
    {
        var result = SendToResolveResult.Parse(1, "   \n  \t  ", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void TracebackWithNoBraces_DoesNotThrow()
    {
        var result = SendToResolveResult.Parse(1, "Traceback (most recent call last):\n  File 'script.py', line 42\n    print('hello')", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void OnlyRightBraceNoLeftBrace_DoesNotThrow()
    {
        var result = SendToResolveResult.Parse(1, "some text }", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void BracePair_DoesNotThrow()
    {
        var result = SendToResolveResult.Parse(1, "}{", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void ValidJsonArray_DoesNotParseAsObject()
    {
        var result = SendToResolveResult.Parse(0, "[1, 2, 3]", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void ObjectMissingAllExpectedKeys_ReturnsDefaultValues()
    {
        var json = """
        {"extra":"data"}
        """;

        var result = SendToResolveResult.Parse(0, json, "");

        Assert.False(result.Ok); // Default ok is false when key missing
        Assert.Equal("unknown", result.Stage); // Missing stage defaults to "unknown"
        Assert.Null(result.Problem);
        Assert.Null(result.TimelineName);
        Assert.Equal(0, result.VideoItems);
        Assert.Equal(0, result.MarkersPlaced);
    }

    // Regression tests for Critical Bug (Fix Round 2): LastJsonObject must guard against
    // calling LastIndexOf with negative index. When { at position 0 fails to parse, the loop
    // evaluates LastIndexOf('{', -1), which throws ArgumentOutOfRangeException.

    [Fact]
    public void PartialJsonStartingAtPosition0_DoesNotThrow()
    {
        // "{invalid json" has { at position 0 and is not valid JSON.
        // The buggy code calls LastIndexOf('{', -1) and throws.
        var result = SendToResolveResult.Parse(1, "{invalid json", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void OnlyOpenBrace_DoesNotThrow()
    {
        // "{" at position 0 is not valid JSON.
        // The buggy code calls LastIndexOf('{', -1) and throws.
        var result = SendToResolveResult.Parse(1, "{", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void IncompleteJsonObject_DoesNotThrow()
    {
        // "{\"a\":" at position 0 is not valid JSON.
        // The buggy code calls LastIndexOf('{', -1) and throws.
        var result = SendToResolveResult.Parse(1, "{\"a\":", "");

        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Stage);
        Assert.NotNull(result.Problem);
    }
}
