using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Assembly;
using StoryForge.Engine.Providers;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The renderer with the real ffmpeg, on small clips made here: the pieces joined by a cut and a
/// crossfade, the clips cropped to the delivery, the narration under them, a clip without sound
/// left out of the mix. Skipped where ffmpeg is not installed.
/// </summary>
public sealed partial class VideoRendererTests : IDisposable
{
    private readonly EngineTestHost _engine = new();

    public void Dispose() => _engine.Dispose();

    private static bool HasFfmpeg =>
        ExecutableResolver.Resolve("ffmpeg", Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT")) is not null;

    [Fact]
    public async Task Clips_are_cropped_joined_and_laid_under_the_narration_and_stay_as_they_were()
    {
        if (!HasFfmpeg)
        {
            return;
        }
        var root = Path.Combine(_engine.DataDirectory, "media");
        Directory.CreateDirectory(Path.Combine(root, "clips"));
        var processes = new ProcessRunner();
        // Three clips a little larger than the delivery, as Minimax makes them (its sizes go in steps of 32).
        await MakeAsync(processes, "-f lavfi -i testsrc=size=320x192:rate=24:duration=5 -f lavfi -i sine=frequency=300:duration=5 -shortest", Path.Combine(root, "clips", "a.mp4"));
        await MakeAsync(processes, "-f lavfi -i testsrc2=size=320x192:rate=24:duration=4", Path.Combine(root, "clips", "b.mp4"));   // no sound
        await MakeAsync(processes, "-f lavfi -i smptebars=size=320x192:rate=24:duration=6 -f lavfi -i sine=frequency=500:duration=6 -shortest", Path.Combine(root, "clips", "c.mp4"));
        await MakeAsync(processes, "-f lavfi -i sine=frequency=200:duration=12 -ac 1 -ar 48000 -c:a pcm_s16le", Path.Combine(root, "narration.wav"));
        var before = File.ReadAllBytes(Path.Combine(root, "clips", "a.mp4"));

        var timeline = new Timeline(
            [Shot("a", 0, 4), Shot("b", 4, 7), Shot("c", 7, 12)],
            [("narration.wav", 12)]);
        var list = new CutList([new Cut("a", CutTransition.Crossfade, 0.4, 0, 0.2, ""), new Cut("b", CutTransition.Cut, 0, -0.2, 0, "")], -20, []);
        Assert.Empty(CutPlan.Problems(list, timeline));
        var plan = CutPlan.Render(list, timeline, new GenerationSize("16:9", 320, 180), 24);
        var host = await _engine.StartAsync(services => services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processes)));
        var renderer = ActivatorUtilities.CreateInstance<FfmpegVideoRenderer>(host.Services);
        var output = Path.Combine(root, "assembly", "video.mp4");

        await renderer.RenderAsync(plan, root, output, 24, new Progress<string>(), CancellationToken.None);

        var probe = (await processes.RunAsync("ffmpeg", $"-hide_banner -nostdin -i \"{output}\"", TimeSpan.FromSeconds(30), CancellationToken.None)).StandardError;
        Assert.Contains("320x180", probe);
        Assert.Contains("Audio: aac", probe);
        var duration = Duration().Match(probe);
        Assert.True(duration.Success, probe);
        Assert.InRange(TimeSpan.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture).TotalSeconds, 11.9, 12.1);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "clips", "a.mp4")));   // the clip files are only read
        Assert.False(File.Exists(output + ".filter.txt"));
    }

    private static CutShot Shot(string key, double start, double end) =>
        new(key, key, "S01", start, end, start - 0.5, end + 0.5, $"clips/{key}.mp4", 1, key, "");

    private static async Task MakeAsync(ProcessRunner processes, string inputs, string output)
    {
        var result = await processes.RunAsync("ffmpeg", $"-y -hide_banner -loglevel error {inputs} \"{output}\"", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [GeneratedRegex(@"Duration: (\d+:\d+:\d+\.\d+)")]
    private static partial Regex Duration();
}
