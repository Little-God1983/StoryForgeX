using System.Globalization;
using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Voice;

/// <summary>
/// The Voice stage: each approved script segment spoken through the voice profile's ComfyUI
/// workflow. A segment's narration goes in parts of at most about 45 s (longer audio goes bad),
/// which are measured and joined into one WAV in the project folder. When each word is heard is
/// estimated from the text (<see cref="WordTimings"/>). Each segment is a cell of its own, and can
/// be spoken again on its own.
/// </summary>
internal sealed class VoiceStage(
    IComfyUi comfy,
    IAudioConverter audio,
    CellReader cells,
    ProfileStore profiles,
    SettingsStore settings,
    ProjectFolders folders,
    TimeProvider clock) : ISegmentWorker
{
    public const string ComfyUi = "ComfyUI";

    /// <summary>The profile input keys the stage fills; a key the profile does not map keeps the workflow's own value.</summary>
    public const string TextKey = "text";
    public const string InstructionKey = "instruction";
    public const string ReferenceKey = "reference_audio";
    public const string SeedKey = "seed";

    private static readonly string[] Placeholders = ["{segment.narration}", "{shot.narration}", "{narration}"];

    public PipelineStage Stage => PipelineStage.Voice;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var segments = await cells.ApprovedSegmentsAsync(context.Project.Id, cancellationToken);
        if (segments.Count == 0)
        {
            throw new StageFailedException("The script has no segments yet. Approve the script, and its segments are spoken.");
        }
        var speaker = await SpeakerAsync(context, cancellationToken);
        var results = new List<CellResult>();
        var clips = new List<VoiceClip>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, version) = segments[i];
            Report(context, ActivityKind.Model, $"{segment.Id} ({i + 1} of {segments.Count}): {segment.Title}");
            var clip = await SpeakAsync(context, speaker, segment, version, cancellationToken);
            var result = new CellResult(segment.Id, StoredJson.Write(clip), VoiceClip.SchemaVersion, Hash(context.Project.Setup, segment, version));
            // Stored at once: a segment takes minutes, and you listen to it while the next is spoken.
            if (context.StoreSegment is { } store)
            {
                await store(result);
            }
            clips.Add(clip);
            results.Add(result);
        }
        var total = clips.Sum(c => c.Seconds);
        Report(context, ActivityKind.Check, $"{clips.Count} segments spoken, voice total {Clock(total)} (target {Clock(context.Project.Setup.Output.TargetSeconds)})");
        return new StageResult(
            StoredJson.Write(new VoiceSheet([.. clips.Select(c => c.SegmentId)], total)),
            VoiceClip.SchemaVersion,
            InputHash.Of(results.Select(r => r.InputHash)),
            results);
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var segments = await cells.ApprovedSegmentsAsync(context.Project.Id, cancellationToken);
        if (segments.FirstOrDefault(s => s.Segment.Id == key) is not { Segment: not null } found)
        {
            throw new StageFailedException($"The script has no segment {key}.");
        }
        var speaker = await SpeakerAsync(context, cancellationToken);
        var clip = await SpeakAsync(context, speaker, found.Segment, found.Version, cancellationToken);
        return new CellResult(key, StoredJson.Write(clip), VoiceClip.SchemaVersion, Hash(context.Project.Setup, found.Segment, found.Version));
    }

    /// <summary>Everything a segment's voice is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, Segment segment, int version) => InputHash.Of(new
    {
        stage = PipelineStage.Voice,
        schema = VoiceClip.SchemaVersion,
        segment = segment.Id,
        version,
        narration = segment.Narration,
        provider = setup.Voice.Provider,
        model = setup.Voice.Model,
        profile = setup.Voice.Profile,
        language = setup.Output.Language,
    });

    /// <summary>What every part is spoken with: the workflow, the profile, the uploaded reference audio, and where the audio goes.</summary>
    private sealed record Speaker(JsonObject Workflow, ProfileContent Profile, string? Reference, string Root, string Folder, string Language, string Project);

    private async Task<Speaker> SpeakerAsync(StageContext context, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (!string.Equals(setup.Voice.Provider, ComfyUi, StringComparison.OrdinalIgnoreCase))
        {
            throw new StageFailedException($"The voice is spoken through ComfyUI for now, and this project uses {setup.Voice.Provider}.");
        }
        var profile = (await profiles.GetVersionAsync(setup.Voice.Profile.ProfileId, setup.Voice.Profile.Version, cancellationToken)).Content;
        var templates = (await settings.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
        var workflow = await WorkflowTemplate.LoadAsync(templates, profile.WorkflowTemplate, cancellationToken);
        if (!WorkflowTemplate.Maps(profile.Inputs, TextKey))
        {
            throw new StageFailedException($"The voice profile does not say where the text goes. Map '{TextKey}' to the TTS node's text, e.g. #11.text.");
        }

        string? reference = null;
        if (WorkflowTemplate.Maps(profile.Inputs, ReferenceKey) && profile.ReferenceFiles.FirstOrDefault() is { } file)
        {
            if (!File.Exists(file))
            {
                throw new StageFailedException($"The voice profile's reference audio is gone ({file}). Add it to the profile again.");
            }
            reference = await comfy.UploadAsync(file, cancellationToken);
            Report(context, ActivityKind.Model, $"reference audio {Path.GetFileName(file)} uploaded to ComfyUI");
        }
        return new Speaker(workflow, profile, reference, await folders.RootAsync(cancellationToken), ProjectFolders.Of(context.Project), setup.Output.Language, setup.Name);
    }

    private async Task<VoiceClip> SpeakAsync(StageContext context, Speaker speaker, Segment segment, int version, CancellationToken cancellationToken)
    {
        var parts = NarrationParts.Split(segment.Narration, speaker.Language);
        if (parts.Count == 0)
        {
            throw new StageFailedException($"{segment.Id} has no narration to speak.");
        }
        var seed = Random.Shared.Next();
        var take = Guid.NewGuid().ToString("N")[..8];
        var relative = Path.Combine(speaker.Folder, "voice", $"{segment.Id}-{take}.wav");
        var target = Path.Combine(speaker.Root, relative);
        var work = Path.Combine(speaker.Root, speaker.Folder, "voice", "parts");
        Directory.CreateDirectory(work);

        var scratch = new List<string>();
        var measured = new List<VoicePart>();
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var name = parts.Count == 1 ? segment.Id : $"{segment.Id} part {i + 1} of {parts.Count}";
                var files = await comfy.RunAsync(
                    Workflow(speaker, parts[i], seed),
                    $"StoryForge · {speaker.Project} · {name}",
                    new Progress(line => Report(context, ActivityKind.Model, $"{name}: {line}")),
                    cancellationToken);
                var spoken = files.FirstOrDefault(f => f.Kind == "audio")
                    ?? throw new StageFailedException("The workflow saved no audio. It needs a node that saves it, e.g. Save Audio.");
                var raw = Path.Combine(work, $"{take}-{i + 1}{Path.GetExtension(spoken.Name)}");
                var wav = Path.Combine(work, $"{take}-{i + 1}.wav");
                scratch.Add(raw);
                scratch.Add(wav);
                await File.WriteAllBytesAsync(raw, spoken.Content, cancellationToken);
                await audio.ToWavAsync(raw, wav, cancellationToken);
                var seconds = Measure(wav);
                measured.Add(new VoicePart(parts[i], Math.Round(seconds, 3)));
                Report(context, ActivityKind.Check, $"{name}: {Clock(seconds)} of audio");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                Wav.Join([.. scratch.Where(f => f.EndsWith(".wav", StringComparison.Ordinal))], target);
            }
            catch (InvalidDataException ex)
            {
                DeleteQuietly(target);
                throw new StageFailedException($"The parts of {segment.Id} could not be joined: {ex.Message}");
            }
        }
        finally
        {
            scratch.ForEach(DeleteQuietly);
        }
        var total = Math.Round(Measure(target), 3);
        if (parts.Count > 1)
        {
            Report(context, ActivityKind.Check, $"{segment.Id}: {parts.Count} parts joined, {Clock(total)}");
        }
        return new VoiceClip(segment.Id, version, relative, total, seed, measured, WordTimings.Estimate(measured));
    }

    private static JsonObject Workflow(Speaker speaker, string text, int seed)
    {
        var values = new Dictionary<string, JsonNode>
        {
            [TextKey] = Text(speaker.Profile.PromptTemplate, text),
            [SeedKey] = seed,
        };
        // Left out when empty: the workflow keeps its own direction and reference voice.
        if (speaker.Profile.Voice.Trim() is { Length: > 0 } instruction)
        {
            values[InstructionKey] = instruction;
        }
        if (speaker.Reference is { } reference)
        {
            values[ReferenceKey] = reference;
        }
        return WorkflowTemplate.Apply(speaker.Workflow, speaker.Profile.Inputs, values);
    }

    /// <summary>The profile's prompt template with the part in its placeholder; without one, the part as it is.</summary>
    private static string Text(string template, string part)
    {
        var placeholder = Placeholders.FirstOrDefault(p => template.Contains(p, StringComparison.Ordinal));
        return placeholder is null ? part : template.Replace(placeholder, part, StringComparison.Ordinal).Trim();
    }

    private static double Measure(string wav)
    {
        try
        {
            return Wav.Seconds(wav);
        }
        catch (InvalidDataException ex)
        {
            throw new StageFailedException($"The converted audio could not be read: {ex.Message}");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still open somewhere; a stray file in the parts folder harms nothing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string Clock(double seconds)
    {
        var whole = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    private void Report(StageContext context, ActivityKind kind, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>Reports at once on the caller's thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
