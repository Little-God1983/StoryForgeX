using System.Globalization;
using System.Xml.Linq;
using StoryForge.Client;

namespace StoryForge.Engine.Assembly;

/// <summary>
/// The rendered cut as an FCPXML 1.10 timeline for an editor (#71): built from the same plan FFmpeg
/// renders, so the two hold the same cut. The clips go on the spine (V1, their own sound on A1 at the
/// cut list's level), the narration on a lane under the first clip (A2), crossfades and dips as cross
/// dissolves. The clips are referenced at their original files, full size, and fill the delivery size.
///
/// <para>What Resolve does with it comes from RawCutX's spikes (Resolve 21.1.1): timecode starts at
/// 01:00:00:00 and spine offsets count from it; a connected clip under any spine piece but the first
/// makes Resolve shuffle the audio tracks, so the narration hangs under the first piece, its offset in
/// that clip's own media time; an audio-only asset's length is sample-exact.</para>
/// </summary>
internal static class FcpxmlTimeline
{
    public const string Version = "1.10";

    // Final Cut's own effect ids: Resolve maps them to its cross dissolve and audio crossfade.
    private const string CrossDissolveUid = "FxPlug:4731E73A-8DAC-4113-9A30-AE85B1761265";
    private const string AudioCrossfadeUid = "FFAudioTransition";

    /// <summary>Silence: FCPXML has no mute, and Resolve ignores srcEnable.</summary>
    private const double SilentDb = -96;

    /// <param name="media">Per file of the plan (relative to <paramref name="root"/>), what it holds.</param>
    /// <param name="name">The timeline's name in the editor.</param>
    public static string Build(RenderPlan plan, IReadOnlyDictionary<string, MediaInfo> media, string root, int fps, string name)
    {
        var pictures = plan.Pictures;
        if (pictures.Count == 0)
        {
            throw new ArgumentException("A timeline needs at least one shot.", nameof(plan));
        }
        string T(long frames) => frames == 0 ? "0s" : $"{frames.ToString(CultureInfo.InvariantCulture)}/{fps}s";
        long hour = 3600L * fps;

        // Where each piece sits on the timeline: the next starts where the join overlaps the last.
        var starts = new long[pictures.Count];
        var ends = new long[pictures.Count];
        for (var i = 0; i < pictures.Count; i++)
        {
            starts[i] = i == 0 ? 0 : ends[i - 1] - plan.Joins[i - 1].Frames;
            ends[i] = starts[i] + pictures[i].Frames;
        }
        // The edit: a hard cut where the next piece starts, a transition's middle otherwise.
        long Edit(int j) => starts[j + 1] + plan.Joins[j].Frames / 2;

        var resources = new XElement("resources");
        var ids = 0;
        string Id() => $"r{++ids}";
        var sequenceFormat = Id();
        resources.Add(Format(sequenceFormat, plan.Size.Width, plan.Size.Height, fps));
        var formats = new Dictionary<(int, int), string> { [(plan.Size.Width, plan.Size.Height)] = sequenceFormat };
        string? crossDissolve = null;
        string? audioCrossfade = null;
        if (plan.Joins.Any(j => j.Transition != CutTransition.Cut && j.Frames > 0))
        {
            resources.Add(new XElement("effect", new XAttribute("id", crossDissolve = Id()), new XAttribute("name", "Cross Dissolve"), new XAttribute("uid", CrossDissolveUid)));
            resources.Add(new XElement("effect", new XAttribute("id", audioCrossfade = Id()), new XAttribute("name", "Audio Crossfade"), new XAttribute("uid", AudioCrossfadeUid)));
        }

        var assets = new Dictionary<string, string>();
        string ClipAsset(string file)
        {
            if (assets.TryGetValue(file, out var known))
            {
                return known;
            }
            var info = media[file];
            var size = (info.Width, info.Height);
            if (!formats.TryGetValue(size, out var format))
            {
                resources.Add(Format(format = Id(), info.Width, info.Height, fps));
                formats[size] = format;
            }
            var asset = new XElement("asset",
                new XAttribute("id", assets[file] = Id()),
                new XAttribute("name", Path.GetFileNameWithoutExtension(file)),
                new XAttribute("start", "0s"),
                new XAttribute("duration", T((long)Math.Round(info.Seconds * fps, MidpointRounding.AwayFromZero))),
                new XAttribute("hasVideo", "1"),
                new XAttribute("videoSources", "1"),
                new XAttribute("format", format),
                new XAttribute("hasAudio", info.HasAudio ? "1" : "0"));
            if (info.HasAudio)
            {
                asset.Add(new XAttribute("audioSources", "1"), new XAttribute("audioChannels", info.AudioChannels), new XAttribute("audioRate", info.AudioRate));
            }
            asset.Add(MediaRep(root, file));
            resources.Add(asset);
            return assets[file];
        }

        var spine = new XElement("spine");
        var volume = plan.ClipSoundDb <= CutList.MinClipSoundDb ? SilentDb : plan.ClipSoundDb;
        for (var i = 0; i < pictures.Count; i++)
        {
            var piece = pictures[i];
            var info = media[piece.File];
            var from = i == 0 ? 0 : Edit(i - 1);
            var to = i == pictures.Count - 1 ? ends[i] : Edit(i);
            var clip = new XElement("asset-clip",
                new XAttribute("ref", ClipAsset(piece.File)),
                new XAttribute("name", Path.GetFileNameWithoutExtension(piece.File)),
                // Absolute against the sequence's timecode, as Final Cut and Resolve write it.
                new XAttribute("offset", T(hour + from)),
                // In the clip's own media: where the piece starts plus how far the edit is into it.
                new XAttribute("start", T(piece.FirstFrame + (from - starts[i]))),
                new XAttribute("duration", T(to - from)),
                new XAttribute("format", formats[(info.Width, info.Height)]),
                new XAttribute("tcFormat", "NDF"));
            if ((info.Width, info.Height) != (plan.Size.Width, plan.Size.Height))
            {
                clip.Add(new XElement("adjust-conform", new XAttribute("type", "fill")));   // cropped like the render, not letterboxed
            }
            if (info.HasAudio)
            {
                clip.Add(new XElement("adjust-volume", new XAttribute("amount", Db(volume))));
            }
            spine.Add(clip);
            if (i < pictures.Count - 1 && plan.Joins[i] is { Frames: > 0 } join && join.Transition != CutTransition.Cut)
            {
                spine.Add(new XElement("transition",
                    new XAttribute("name", "Cross Dissolve"),
                    new XAttribute("offset", T(hour + starts[i + 1])),
                    new XAttribute("duration", T(join.Frames)),
                    new XElement("filter-video", new XAttribute("ref", crossDissolve!), new XAttribute("name", "Cross Dissolve")),
                    new XElement("filter-audio", new XAttribute("ref", audioCrossfade!), new XAttribute("name", "Audio Crossfade"))));
            }
        }

        // The narration: one audio-only asset per voice file, butt-joined on lane 1 under the first clip.
        var first = spine.Elements("asset-clip").First();
        var anchor = pictures[0].FirstFrame;   // the first clip's media frame at the timeline's start
        var total = ends[^1];
        var heard = 0.0;
        foreach (var file in plan.Narration)
        {
            var info = media[file];
            var at = Math.Min(total, (long)Math.Round(heard * fps, MidpointRounding.AwayFromZero));
            heard += info.Seconds;
            // Never past the file's last whole frame: rounded from the running total, a piece could be a
            // frame longer than its file, and Resolve cuts that back on import.
            var until = Math.Min(Math.Min(total, (long)Math.Round(heard * fps, MidpointRounding.AwayFromZero)), at + (long)Math.Floor(info.Seconds * fps));
            if (until <= at || !info.HasAudio)
            {
                continue;
            }
            var asset = Id();
            resources.Add(new XElement("asset",
                new XAttribute("id", asset),
                new XAttribute("name", Path.GetFileNameWithoutExtension(file)),
                new XAttribute("start", "0s"),
                new XAttribute("duration", $"{(long)Math.Round(info.Seconds * info.AudioRate)}/{info.AudioRate}s"),
                new XAttribute("hasVideo", "0"),
                new XAttribute("hasAudio", "1"),
                new XAttribute("audioSources", "1"),
                new XAttribute("audioChannels", info.AudioChannels),
                new XAttribute("audioRate", info.AudioRate),
                MediaRep(root, file)));
            first.Add(new XElement("asset-clip",
                new XAttribute("ref", asset),
                new XAttribute("lane", "1"),
                new XAttribute("offset", T(anchor + at)),
                new XAttribute("name", Path.GetFileNameWithoutExtension(file)),
                new XAttribute("start", "0s"),
                new XAttribute("duration", T(until - at))));
        }

        var document = new XElement("fcpxml",
            new XAttribute("version", Version),
            resources,
            new XElement("library",
                new XElement("event",
                    new XAttribute("name", name),
                    new XElement("project",
                        new XAttribute("name", name),
                        new XElement("sequence",
                            new XAttribute("format", sequenceFormat),
                            new XAttribute("duration", T(total)),
                            new XAttribute("tcStart", T(hour)),
                            new XAttribute("tcFormat", "NDF"),
                            new XAttribute("audioLayout", "stereo"),
                            new XAttribute("audioRate", "48k"),
                            spine)))));
        // By hand, not XDocument.Save(StringWriter), which stamps utf-16 into the declaration; LF throughout.
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n" + document.ToString().Replace("\r\n", "\n") + "\n";
    }

    private static XElement Format(string id, int width, int height, int fps) => new("format",
        new XAttribute("id", id),
        new XAttribute("name", $"FFVideoFormat{width}x{height}p{fps}"),
        new XAttribute("frameDuration", $"1/{fps}s"),
        new XAttribute("width", width),
        new XAttribute("height", height),
        new XAttribute("colorSpace", "1-1-1"));

    /// <summary>A percent-encoded file:/// URI, so spaces and "–" survive the trip into the editor.</summary>
    private static XElement MediaRep(string root, string file) => new("media-rep",
        new XAttribute("kind", "original-media"),
        new XAttribute("src", new Uri(Path.Combine(root, file)).AbsoluteUri));

    private static string Db(double db) => db.ToString("0.##", CultureInfo.InvariantCulture) + "dB";
}
