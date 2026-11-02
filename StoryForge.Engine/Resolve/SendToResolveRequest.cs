using System.Globalization;
using System.Text.Json;

namespace StoryForge.Engine.Resolve;

/// <summary>A ruler marker the script places after the import: Resolve's FCPXML import drops the file's own.</summary>
/// <param name="Frame">Counted from the timeline's first frame, not from its start timecode.</param>
/// <param name="Color">One of Resolve's marker colours, e.g. "Blue".</param>
internal sealed record ResolveMarker(int Frame, string Color, string Text);

/// <summary>What StoryForge X hands the script, as a temp JSON file (taken over from RawCutX).</summary>
internal sealed record SendToResolveRequest(string ProjectName, string TimelinePath, string FrameRate, bool PlaceMarkers, IReadOnlyList<ResolveMarker> Markers)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// The rate as Resolve's <c>timelineFrameRate</c> setting wants it: "24", "29.97", "23.976".
    /// Invariant culture: a German Windows would otherwise send "29,97".
    /// </summary>
    public static string FrameRateText(int fpsNum, int fpsDen)
    {
        if (fpsDen <= 0)
        {
            return "0";
        }
        return fpsNum % fpsDen == 0
            ? (fpsNum / fpsDen).ToString(CultureInfo.InvariantCulture)
            : ((double)fpsNum / fpsDen).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
