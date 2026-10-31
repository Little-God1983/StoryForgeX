using System.Xml.Linq;
using StoryForge.Client;
using StoryForge.Engine.Assembly;

namespace StoryForge.Engine.Tests;

/// <summary>#71: the rendered cut as an FCPXML timeline for Resolve, built from the render's own plan.</summary>
public sealed class FcpxmlTimelineTests
{
    private const string Root = @"D:\StoryForge X";
    private const int Fps = 24;

    private static readonly MediaInfo Clip = new(7.29, 1920, 1088, 32000, 2);
    private static readonly MediaInfo Voice = new(15.76, 0, 0, 48000, 1);

    /// <summary>
    /// Three shots: a hard cut after the first, a 12-frame crossfade after the second. The pieces
    /// overlap by the transition, as the renderer joins them; the narration is two voice files.
    /// </summary>
    private static RenderPlan Plan(double clipSoundDb = -20) => new(
        [new PicturePart(@"p\clips\S01-a.mp4", 12, 100), new PicturePart(@"p\clips\S01-b.mp4", 12, 106), new PicturePart(@"p\clips\S02-a.mp4", 6, 94)],
        [(CutTransition.Cut, 0), (CutTransition.Crossfade, 12)],
        [],
        [@"p\voice\S01.wav", @"p\voice\S02.wav"],
        12.0,
        new GenerationSize("16:9", 1920, 1080),
        clipSoundDb);

    private static Dictionary<string, MediaInfo> Media() => new()
    {
        [@"p\clips\S01-a.mp4"] = Clip,
        [@"p\clips\S01-b.mp4"] = Clip,
        [@"p\clips\S02-a.mp4"] = Clip with { AudioRate = 0, AudioChannels = 0 },
        [@"p\voice\S01.wav"] = Voice with { Seconds = 5 },
        [@"p\voice\S02.wav"] = Voice with { Seconds = 7 },
    };

    private static XElement Build(RenderPlan? plan = null) =>
        XElement.Parse(FcpxmlTimeline.Build(plan ?? Plan(), Media(), Root, Fps, "Dark Eye – DSA lore"));

    private static List<XElement> Spine(XElement doc) => [.. doc.Descendants("spine").Single().Elements()];

    [Fact]
    public void The_file_is_FCPXML_1_10_in_UTF8_with_LF_line_ends()
    {
        var text = FcpxmlTimeline.Build(Plan(), Media(), Root, Fps, "Dark Eye – DSA lore");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n<fcpxml version=\"1.10\">", text);
        Assert.DoesNotContain("\r", text);
        Assert.Equal(["Dark Eye – DSA lore", "Dark Eye – DSA lore"], Build().Descendants().Where(e => e.Name == "event" || e.Name == "project").Select(e => (string)e.Attribute("name")!));
    }

    [Fact]
    public void The_sequence_is_the_delivery_size_from_one_hour_for_the_whole_cut()
    {
        var doc = Build();
        var sequence = doc.Descendants("sequence").Single();
        var format = doc.Descendants("format").Single(f => (string?)f.Attribute("id") == (string?)sequence.Attribute("format"));

        Assert.Equal(("1920", "1080", "1/24s"), ((string)format.Attribute("width")!, (string)format.Attribute("height")!, (string)format.Attribute("frameDuration")!));
        Assert.Equal("86400/24s", (string?)sequence.Attribute("tcStart"));   // 01:00:00:00
        // 100 + 106 + 94 frames, less the 12 the crossfade overlaps.
        Assert.Equal("288/24s", (string?)sequence.Attribute("duration"));
    }

    [Fact]
    public void Each_shot_is_one_clip_from_its_edit_to_the_next_with_its_in_point_inside_the_clip()
    {
        var clips = Spine(Build()).Where(e => e.Name == "asset-clip").ToList();

        // Hard cut at frame 100; the crossfade covers 194–206 with its edit at 200.
        Assert.Equal(["86400/24s", "86500/24s", "86600/24s"], clips.Select(c => (string)c.Attribute("offset")!));
        Assert.Equal(["100/24s", "100/24s", "88/24s"], clips.Select(c => (string)c.Attribute("duration")!));
        // The third piece starts at its frame 6 at 194; the edit is 6 frames into it.
        Assert.Equal(["12/24s", "12/24s", "12/24s"], clips.Select(c => (string)c.Attribute("start")!));
    }

    [Fact]
    public void A_crossfade_or_dip_is_a_cross_dissolve_over_the_overlap_and_a_hard_cut_none()
    {
        var doc = Build();
        var spine = Spine(doc);

        var transition = Assert.Single(spine, e => e.Name == "transition");
        Assert.Equal(2, spine.IndexOf(transition));   // between the second and the third clip
        Assert.Equal(("86594/24s", "12/24s"), ((string)transition.Attribute("offset")!, (string)transition.Attribute("duration")!));
        var effect = doc.Descendants("effect").Single(e => (string?)e.Attribute("id") == (string?)transition.Element("filter-video")!.Attribute("ref"));
        Assert.Equal("FxPlug:4731E73A-8DAC-4113-9A30-AE85B1761265", (string?)effect.Attribute("uid"));

        var dip = Plan() with { Joins = [(CutTransition.DipToBlack, 6), (CutTransition.Cut, 0)] };
        Assert.Single(Spine(Build(dip)), e => e.Name == "transition");
        var cutsOnly = Plan() with { Joins = [(CutTransition.Cut, 0), (CutTransition.Cut, 0)], Pictures = [.. Plan().Pictures.Select(p => p with { Frames = 100 })] };
        Assert.DoesNotContain(Spine(Build(cutsOnly)), e => e.Name == "transition");
        Assert.Empty(Build(cutsOnly).Descendants("effect"));
    }

    [Fact]
    public void Clips_larger_than_the_delivery_fill_it_and_their_sound_is_at_the_cut_lists_level()
    {
        var clips = Spine(Build()).Where(e => e.Name == "asset-clip").ToList();

        Assert.All(clips, c => Assert.Equal("fill", (string?)c.Element("adjust-conform")?.Attribute("type")));
        Assert.Equal(["-20dB", "-20dB", null], clips.Select(c => (string?)c.Element("adjust-volume")?.Attribute("amount")));
        // At the bottom of the range the clips' sound is off, which FCPXML can only say as -96 dB.
        Assert.Equal("-96dB", (string?)Spine(Build(Plan(clipSoundDb: CutList.MinClipSoundDb))).First().Element("adjust-volume")?.Attribute("amount"));
    }

    [Fact]
    public void Each_clip_is_an_asset_at_its_own_size_with_its_own_sound_and_a_file_url()
    {
        var doc = Build();
        var assets = doc.Descendants("asset").Where(a => (string?)a.Attribute("hasVideo") == "1").ToList();

        Assert.Equal(3, assets.Count);
        var first = assets[0];
        var format = doc.Descendants("format").Single(f => (string?)f.Attribute("id") == (string?)first.Attribute("format"));
        Assert.Equal(("1920", "1088"), ((string)format.Attribute("width")!, (string)format.Attribute("height")!));
        Assert.Equal(("175/24s", "1", "2", "32000"), ((string)first.Attribute("duration")!, (string)first.Attribute("hasAudio")!,
            (string)first.Attribute("audioChannels")!, (string)first.Attribute("audioRate")!));
        Assert.Equal("file:///D:/StoryForge%20X/p/clips/S01-a.mp4", (string?)first.Element("media-rep")?.Attribute("src"));
        Assert.Equal("0", (string?)assets[2].Attribute("hasAudio"));
        Assert.Null(assets[2].Attribute("audioRate"));
    }

    [Fact]
    public void The_narration_lies_on_lane_one_under_the_first_clip_in_its_media_time_butt_joined()
    {
        var doc = Build();
        var first = Spine(doc).First();

        var voices = first.Elements("asset-clip").ToList();
        Assert.Equal(["1", "1"], voices.Select(v => (string)v.Attribute("lane")!));
        // The first clip's media frame 12 is the timeline's start; the second file follows at 5 s.
        Assert.Equal(["12/24s", "132/24s"], voices.Select(v => (string)v.Attribute("offset")!));
        Assert.Equal(["120/24s", "168/24s"], voices.Select(v => (string)v.Attribute("duration")!));
        Assert.Empty(Spine(doc).Skip(1).SelectMany(e => e.Elements("asset-clip")));   // under the first piece only
        // Audio-only, sample-exact, after the clip's own adjustments.
        var asset = doc.Descendants("asset").Single(a => (string?)a.Attribute("id") == (string?)voices[0].Attribute("ref"));
        Assert.Equal(("0", "240000/48000s", "1", "48000"), ((string)asset.Attribute("hasVideo")!, (string)asset.Attribute("duration")!,
            (string)asset.Attribute("audioChannels")!, (string)asset.Attribute("audioRate")!));
        Assert.Equal(["adjust-conform", "adjust-volume", "asset-clip", "asset-clip"], first.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Narration_past_the_picture_is_cut_at_its_end()
    {
        var media = Media();
        media[@"p\voice\S02.wav"] = Voice with { Seconds = 30 };

        var doc = XElement.Parse(FcpxmlTimeline.Build(Plan(), media, Root, Fps, "x"));

        Assert.Equal("168/24s", (string?)Spine(doc).First().Elements("asset-clip").Last().Attribute("duration"));
    }

    [Theory]
    [InlineData("  Duration: 00:00:07.29, start: 0.000000, bitrate: 10762 kb/s\n  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1088, 10624 kb/s, 24 fps\n  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 32000 Hz, stereo, fltp, 127 kb/s (default)",
        7.29, 1920, 1088, 32000, 2)]
    [InlineData("  Duration: 00:00:15.76, bitrate: 768 kb/s\n  Stream #0:0: Audio: pcm_s16le ([1][0][0][0] / 0x0001), 48000 Hz, 1 channels, s16, 768 kb/s",
        15.76, 0, 0, 48000, 1)]
    [InlineData("  Duration: 01:02:03.50, start: 0\n  Stream #0:0: Video: h264, yuv420p, 1280x720 [SAR 1:1 DAR 16:9], 24 fps",
        3723.5, 1280, 720, 0, 0)]
    [InlineData("  Duration: 00:00:02.00\n  Stream #0:0: Audio: aac, 44100 Hz, 5.1(side), fltp", 2.0, 0, 0, 44100, 6)]
    public void What_ffmpeg_lists_for_a_file_is_read_as_its_length_picture_and_sound(string streams, double seconds, int width, int height, int rate, int channels)
    {
        Assert.Equal(new MediaInfo(seconds, width, height, rate, channels), MediaInfo.Parse(streams));
    }

    [Fact]
    public void A_file_ffmpeg_lists_no_stream_for_is_not_read()
    {
        Assert.Null(MediaInfo.Parse("D:\\x.mp4: Invalid data found when processing input"));
    }
}
