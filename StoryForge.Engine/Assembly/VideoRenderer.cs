using System.Globalization;
using System.Text;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Assembly;

/// <summary>Renders a cut list's plan into the finished video.</summary>
internal interface IVideoRenderer
{
    /// <param name="root">The projects folder the plan's files are relative to.</param>
    /// <param name="output">The video to write, a full path.</param>
    /// <exception cref="StageFailedException">ffmpeg is missing, or could not render the video.</exception>
    Task RenderAsync(RenderPlan plan, string root, string output, int fps, IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Through ffmpeg (Settings → FFmpeg), in one run: each clip's piece cropped to the delivery size
/// (scaled up only when it is smaller), the pieces joined by cut, crossfade or dip to black, the
/// narration as it is and the clips' own sound under it. The clip files are only read.
/// </summary>
internal sealed class FfmpegVideoRenderer(IProcessRunner processes, SettingsStore settings) : IVideoRenderer
{
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public async Task RenderAsync(RenderPlan plan, string root, string output, int fps, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var ffmpeg = (await settings.LoadAsync(cancellationToken)).Ffmpeg.Executable;
        var clips = plan.Pictures.Select(p => p.File).Concat(plan.Sounds.Select(s => s.File)).Distinct().ToList();
        var withSound = new HashSet<string>();
        if (plan.ClipSoundDb > CutList.MinClipSoundDb)
        {
            foreach (var clip in clips)
            {
                if (await HasSoundAsync(ffmpeg, Path.Combine(root, clip), cancellationToken))
                {
                    withSound.Add(clip);
                }
            }
        }
        var script = Graph(plan, clips, withSound, fps);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var graphFile = output + ".filter.txt";
        await File.WriteAllTextAsync(graphFile, script, cancellationToken);
        var rendered = false;
        try
        {
            var arguments = new StringBuilder("-y -hide_banner -loglevel error -nostdin");
            foreach (var file in clips.Concat(plan.Narration))
            {
                arguments.Append(CultureInfo.InvariantCulture, $" -i \"{Path.Combine(root, file)}\"");
            }
            arguments.Append(CultureInfo.InvariantCulture,
                $" -filter_complex_script \"{graphFile}\" -map \"[vout]\" -map \"[aout]\" -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -r {fps} -c:a aac -b:a 192k -movflags +faststart -t {Seconds(plan.Seconds)} \"{output}\"");
            progress.Report($"rendering {plan.Pictures.Count} shots, {plan.Seconds:0} s, with FFmpeg");
            var result = await RunAsync(ffmpeg, arguments.ToString(), RenderTimeout, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                var why = result.StandardError.Trim().Split('\n').LastOrDefault()?.Trim();
                throw new StageFailedException($"ffmpeg could not render the video{(string.IsNullOrEmpty(why) ? "" : $": {why}")}.");
            }
            rendered = true;
        }
        finally
        {
            // Failed or cancelled: what ffmpeg wrote so far is no video.
            foreach (var leftover in rendered ? [graphFile] : new[] { graphFile, output })
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                    // ffmpeg, just killed, may still hold it: the failure or Cancel stays what is said.
                }
            }
        }
    }

    /// <summary>The filter graph: inputs are the clips in <paramref name="clips"/> order, then the narration files.</summary>
    internal static string Graph(RenderPlan plan, List<string> clips, IReadOnlySet<string> withSound, int fps)
    {
        var (w, h) = (plan.Size.Width, plan.Size.Height);
        var graph = new StringBuilder();
        void Line(FormattableString text) => graph.AppendLine(text.ToString(CultureInfo.InvariantCulture) + ";");

        // Each piece: at the frame rate, cropped to the delivery (scaled up only if smaller), its frames
        // cut out; a clip that ends early holds its last frame.
        for (var i = 0; i < plan.Pictures.Count; i++)
        {
            var part = plan.Pictures[i];
            Line($"[{clips.IndexOf(part.File)}:v]fps={fps},scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,format=yuv420p,tpad=stop_mode=clone:stop_duration=5,trim=start_frame={part.FirstFrame}:end_frame={part.FirstFrame + part.Frames},setpts=PTS-STARTPTS,settb=AVTB[p{i}]");
        }
        var joined = "p0";
        var frames = plan.Pictures[0].Frames;
        for (var j = 0; j < plan.Joins.Count; j++)
        {
            var (transition, overlap) = plan.Joins[j];
            var next = $"p{j + 1}";
            if (transition == CutTransition.Cut || overlap <= 0)
            {
                Line($"[{joined}][{next}]concat=n=2:v=1:a=0[j{j}]");
            }
            else
            {
                var kind = transition == CutTransition.DipToBlack ? "fadeblack" : "fade";
                Line($"[{joined}][{next}]xfade=transition={kind}:duration={Seconds((double)overlap / fps)}:offset={Seconds((double)(frames - overlap) / fps)}[j{j}]");
            }
            frames += plan.Pictures[j + 1].Frames - Math.Max(0, transition == CutTransition.Cut ? 0 : overlap);
            joined = $"j{j}";
        }
        Line($"[{joined}]null[vout]");

        // The narration as it is, the segments one after another.
        const string Stereo = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        var narration = string.Concat(plan.Narration.Select((_, n) => $"[{clips.Count + n}:a]"));
        Line($"{narration}concat=n={plan.Narration.Count}:v=0:a=1,{Stereo}[narr]");

        // Each clip's own sound, placed on the timeline, faded at its cuts, as quiet as the cut list says.
        var mixed = new List<string>();
        for (var i = 0; i < plan.Sounds.Count; i++)
        {
            var part = plan.Sounds[i];
            if (!withSound.Contains(part.File) || part.To - part.From <= 0)
            {
                continue;
            }
            var fades = new StringBuilder();
            if (part.FadeIn > 0)
            {
                fades.Append(CultureInfo.InvariantCulture, $",afade=t=in:d={Seconds(part.FadeIn)}");
            }
            if (part.FadeOut > 0)
            {
                fades.Append(CultureInfo.InvariantCulture, $",afade=t=out:st={Seconds(Math.Max(0, part.To - part.From - part.FadeOut))}:d={Seconds(part.FadeOut)}");
            }
            var delay = (long)Math.Round(Math.Max(0, part.At) * 1000);
            Line($"[{clips.IndexOf(part.File)}:a]{Stereo},apad=pad_dur=5,atrim=start={Seconds(Math.Max(0, part.From))}:end={Seconds(part.To)},asetpts=PTS-STARTPTS{fades},volume={Seconds(plan.ClipSoundDb)}dB,adelay=delays={delay}:all=1[s{i}]");
            mixed.Add($"[s{i}]");
        }
        graph.Append(mixed.Count == 0
            ? "[narr]anull[aout]"
            : $"[narr]{string.Concat(mixed)}amix=inputs={mixed.Count + 1}:duration=first:normalize=0:dropout_transition=0[aout]");
        return graph.ToString();
    }

    private async Task<bool> HasSoundAsync(string ffmpeg, string clip, CancellationToken cancellationToken)
    {
        // With no output named ffmpeg lists the streams and stops: "Stream #0:1: Audio: aac …".
        var result = await RunAsync(ffmpeg, $"-hide_banner -nostdin -i \"{clip}\"", ProbeTimeout, cancellationToken);
        if (!result.StandardError.Contains("Stream #", StringComparison.Ordinal))
        {
            var why = result.StandardError.Trim().Split('\n').LastOrDefault()?.Trim();
            throw new StageFailedException($"ffmpeg could not read the clip '{Path.GetFileName(clip)}'{(string.IsNullOrEmpty(why) ? "" : $": {why}")}.");
        }
        return result.StandardError.Contains("Audio:", StringComparison.Ordinal);
    }

    private async Task<ProcessRunResult> RunAsync(string ffmpeg, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await processes.RunAsync(ffmpeg, arguments, timeout, cancellationToken);
        }
        catch (ExecutableNotFoundException)
        {
            throw new StageFailedException($"ffmpeg was not found ('{ffmpeg}'). Install it, or set its path in Settings → FFmpeg.");
        }
        catch (TimeoutException)
        {
            throw new StageFailedException($"ffmpeg took longer than {timeout.TotalMinutes:0} minutes.");
        }
    }

    private static string Seconds(double seconds) => Math.Round(seconds, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
