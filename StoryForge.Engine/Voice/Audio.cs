using System.Buffers.Binary;
using System.Globalization;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Voice;

/// <summary>Turns whatever the TTS workflow saved (mp3, flac, …) into the WAV every part is joined as.</summary>
internal interface IAudioConverter
{
    /// <exception cref="Pipeline.StageFailedException">ffmpeg is missing or could not read the file.</exception>
    Task ToWavAsync(string source, string target, CancellationToken cancellationToken);
}

/// <summary>Through ffmpeg (Settings → FFmpeg): mono, 48 kHz, 16-bit, so parts join without converting again.</summary>
internal sealed class FfmpegAudioConverter(IProcessRunner processes, SettingsStore settings) : IAudioConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task ToWavAsync(string source, string target, CancellationToken cancellationToken)
    {
        var ffmpeg = (await settings.LoadAsync(cancellationToken)).Ffmpeg.Executable;
        ProcessRunResult result;
        try
        {
            result = await processes.RunAsync(
                ffmpeg,
                string.Create(CultureInfo.InvariantCulture, $"-y -hide_banner -loglevel error -i \"{source}\" -ac 1 -ar {Wav.SampleRate} -c:a pcm_s16le \"{target}\""),
                Timeout,
                cancellationToken);
        }
        catch (ExecutableNotFoundException)
        {
            throw new Pipeline.StageFailedException($"ffmpeg was not found ('{ffmpeg}'). Install it, or set its path in Settings → FFmpeg.");
        }
        catch (TimeoutException)
        {
            throw new Pipeline.StageFailedException($"ffmpeg took longer than {Timeout.TotalMinutes:0} minutes to convert the audio.");
        }
        if (result.ExitCode != 0 || !File.Exists(target))
        {
            var why = result.StandardError.Trim().Split('\n').LastOrDefault()?.Trim();
            throw new Pipeline.StageFailedException($"ffmpeg could not read the audio ComfyUI gave{(string.IsNullOrEmpty(why) ? "" : $": {why}")}.");
        }
    }
}

/// <summary>The few things done to WAV files here: measure one, and join parts of the same format.</summary>
internal static class Wav
{
    public const int SampleRate = 48000;

    /// <summary>How long the audio is, from its header.</summary>
    /// <exception cref="InvalidDataException">Not a PCM WAV file.</exception>
    public static double Seconds(string path)
    {
        var (format, data) = Read(path);
        var byteRate = BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(8, 4));
        return byteRate == 0 ? 0 : (double)data.Length / byteRate;
    }

    /// <summary>The parts one after another in one file; they must share one format (see <see cref="FfmpegAudioConverter"/>).</summary>
    /// <exception cref="InvalidDataException">A part is not a WAV file, or the parts differ in format.</exception>
    public static void Join(IReadOnlyList<string> parts, string target)
    {
        var read = parts.Select(Read).ToList();
        var format = read[0].Format;
        if (read.Any(r => !r.Format.AsSpan().SequenceEqual(format)))
        {
            throw new InvalidDataException("The parts are not all in the same audio format.");
        }
        var length = read.Sum(r => (long)r.Data.Length);
        using var output = File.Create(target);
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8);
        writer.Write((uint)(4 + 8 + format.Length + 8 + length));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(format.Length);
        writer.Write(format);
        writer.Write("data"u8);
        writer.Write((uint)length);
        foreach (var (_, data) in read)
        {
            writer.Write(data);
        }
    }

    private static (byte[] Format, byte[] Data) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a WAV file.");
        }
        byte[]? format = null;
        byte[]? data = null;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4)), (uint)(bytes.Length - at - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                format = bytes.AsSpan(at + 8, size).ToArray();
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.AsSpan(at + 8, size).ToArray();
            }
            at += 8 + size + (size & 1);   // chunks are padded to an even length
        }
        if (format is null || format.Length < 16 || data is null)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no audio in it.");
        }
        return (format, data);
    }
}
