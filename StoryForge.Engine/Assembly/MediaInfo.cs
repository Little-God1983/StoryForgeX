using System.Globalization;
using System.Text.RegularExpressions;

namespace StoryForge.Engine.Assembly;

/// <summary>What a timeline file has to declare about a media file: its length, picture and sound.</summary>
/// <param name="Width">0 for a file with no picture.</param>
/// <param name="AudioRate">0 for a file with no sound.</param>
internal sealed record MediaInfo(double Seconds, int Width, int Height, int AudioRate, int AudioChannels)
{
    public bool HasVideo => Width > 0 && Height > 0;

    public bool HasAudio => AudioRate > 0 && AudioChannels > 0;

    /// <summary>
    /// From the stream list ffmpeg prints for <c>ffmpeg -i file</c>: "Duration: 00:00:07.29", "Video:
    /// h264 …, 1920x1088, …", "Audio: aac …, 32000 Hz, stereo, …". Null when it lists no stream.
    /// </summary>
    public static MediaInfo? Parse(string streams)
    {
        if (!streams.Contains("Stream #", StringComparison.Ordinal))
        {
            return null;
        }
        var duration = Regex.Match(streams, @"Duration: (\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
        var seconds = duration.Success
            ? int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        var video = Regex.Match(streams, @"Stream #[^\n]*Video: [^\n]*?, (\d{2,5})x(\d{2,5})");
        var audio = Regex.Match(streams, @"Stream #[^\n]*Audio: [^\n]*?, (\d+) Hz, ([^,\n]+)");
        return new MediaInfo(
            seconds,
            video.Success ? int.Parse(video.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            video.Success ? int.Parse(video.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            audio.Success ? int.Parse(audio.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            audio.Success ? Channels(audio.Groups[2].Value.Trim()) : 0);
    }

    /// <summary>"mono", "stereo", "5.1(side)", "1 channels".</summary>
    private static int Channels(string layout)
    {
        if (layout.StartsWith("mono", StringComparison.Ordinal))
        {
            return 1;
        }
        if (layout.StartsWith("stereo", StringComparison.Ordinal))
        {
            return 2;
        }
        var count = Regex.Match(layout, @"^(\d+) channels");
        if (count.Success)
        {
            return int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var surround = Regex.Match(layout, @"^(\d+)\.(\d+)");
        return surround.Success
            ? int.Parse(surround.Groups[1].Value, CultureInfo.InvariantCulture) + int.Parse(surround.Groups[2].Value, CultureInfo.InvariantCulture)
            : 2;
    }
}
