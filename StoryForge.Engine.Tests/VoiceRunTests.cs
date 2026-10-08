using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Voice stage in the pipeline, with a scripted ComfyUI and ffmpeg: it starts when the script is
/// approved, speaks each segment in parts of at most 45 s, joins them into one WAV in the project
/// folder, and each segment is spoken again or approved on its own.
/// </summary>
public sealed class VoiceRunTests : IDisposable
{
    /// <summary>What the fake TTS takes per word: a 150-word segment is a minute long.</summary>
    private const double SecondsPerWord = 0.4;

    private readonly EngineTestHost _engine = new();
    private readonly ScriptRunTests.FakeScriptAgent _script = new();
    private readonly FakeComfy _comfy = new();
    private readonly string _templates;
    private readonly string _projects;

    public VoiceRunTests()
    {
        _templates = Path.Combine(_engine.DataDirectory, "templates");
        _projects = Path.Combine(_engine.DataDirectory, "media");
        Directory.CreateDirectory(_templates);
        File.WriteAllText(Path.Combine(_templates, "voice.json"), ApiWorkflow);
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>Breeze voice direction, exported for the API: a reference voice, the text, a direction, a saved mp3.</summary>
    private const string ApiWorkflow = """
        {
          "1": { "class_type": "ITLBreezeTTSLoader", "inputs": { "attention": "sdpa", "fast_path": false } },
          "2": { "class_type": "LoadAudio", "inputs": { "audio": "Amira.mp3" } },
          "11": { "class_type": "ITLBreezeTTSVoiceDirection", "inputs": { "model": ["1", 0], "reference_audio": ["2", 0], "reference_text": "", "text": "", "instruction": "", "seed": 1, "cfg_scale": 4, "unload_after": false } },
          "4": { "class_type": "SaveAudioAdvanced", "inputs": { "audio": ["11", 0], "filename_prefix": "audio/BreezeTTS" } }
        }
        """;

    private Task<IStoryForgeClient> StartAsync() => _engine.StartClientAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new ScriptRunTests.ThreeFacts()));
        services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(_script));
        services.Replace(ServiceDescriptor.Singleton<IComfyUi>(_comfy));
        services.Replace(ServiceDescriptor.Singleton<IAudioConverter>(new FakeConverter()));
    });

    /// <summary>
    /// A project whose script is written and approved (no Script gate), with the voice profile set
    /// up for the workflow above, so the voice starts by itself.
    /// </summary>
    private async Task<Project> ScriptApprovedAsync(IStoryForgeClient client, bool voiceGate = false, string workflow = "voice.json", bool scriptGate = false)
    {
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with
        {
            ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = _templates },
            Paths = new PathSettings(_projects),
        });
        var reference = Path.Combine(_engine.DataDirectory, "amira.mp3");
        await File.WriteAllTextAsync(reference, "a few seconds of a voice");
        var profile = await client.CreateProfileAsync(ProfileKind.Voice, "Amira");
        var starter = await client.GetProfileVersionAsync(profile.Id);
        var saved = await client.SaveProfileVersionAsync(profile.Id, starter.Content with
        {
            WorkflowTemplate = workflow,
            Voice = "a calm, warm documentary narrator",
            ReferenceFiles = [await client.ImportReferenceFileAsync(reference)],
            Inputs = [new("text", "#11.text"), new("instruction", "#11.instruction"), new("reference_audio", "#2.audio"), new("seed", "#11.seed")],
        });
        _script.Answer(ScriptRunTests.Good());
        var project = await ScriptRunTests.ApprovedFactsAsync(client, s => s with
        {
            Gates = [.. s.Gates.Where(g => g != PipelineStage.Script || scriptGate), .. voiceGate ? new[] { PipelineStage.Voice } : []],
            Voice = s.Voice with { Profile = new ProfileRef(profile.Id, saved.Version) },
        });
        if (scriptGate)
        {
            await ScriptRunTests.WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.State == StageState.NeedsReview);
            await client.ApproveSegmentsAsync(project.Id, PipelineStage.Script);
        }
        return project;
    }

    private static Task<VoiceView> VoiceAsync(IStoryForgeClient client, Guid projectId, params StageState[] states) =>
        ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(projectId), v => states.Contains(v.State));

    [Fact]
    public async Task An_approved_script_is_spoken_segment_by_segment_into_the_project_folder()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);

        var voice = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);

        Assert.Null(voice.Error);
        Assert.Equal(["S01", "S02", "S03", "S04"], voice.Segments.Select(s => s.Id));
        Assert.All(voice.Segments, s =>
        {
            Assert.Equal((StageState.Approved, 1), (s.State, s.Version));
            Assert.Equal(60, s.Seconds, 3);
            Assert.True(File.Exists(s.AudioPath));
            Assert.StartsWith(Path.Combine(_projects, $"Soul Coins – BG3 lore ({project.Id.ToString("N")[..8]})", "voice"), s.AudioPath);
            Assert.Equal(150, s.Words.Count);
        });
        Assert.Equal(240, voice.Seconds, 3);
        // No scratch is left behind: the parts were joined.
        Assert.Equal(
            voice.Segments.Select(s => s.AudioPath).Order(),
            Directory.GetFiles(Path.GetDirectoryName(voice.Segments[0].AudioPath)!, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task No_part_sent_to_ComfyUI_is_longer_than_45_seconds_and_the_parts_join_up()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);

        var voice = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);

        // 150 words a segment at 150 a minute: 60 s, so two parts each (112 words is 45 s).
        Assert.Equal(8, _comfy.Texts.Count);
        Assert.All(_comfy.Texts, text => Assert.InRange(Words(text), 1, 112));
        var s01 = voice.Segments[0];
        Assert.Equal(2, s01.Parts.Count);
        Assert.Equal(112 * SecondsPerWord, s01.Parts[0].Seconds, 3);
        Assert.Equal(38 * SecondsPerWord, s01.Parts[1].Seconds, 3);
        Assert.Equal(string.Join(' ', Enumerable.Repeat("word", 150)), string.Join(' ', s01.Parts.Select(p => p.Text)));
        // The words of the second part start where the first part's audio ends.
        Assert.True(s01.Words[112].Start >= 112 * SecondsPerWord);
        Assert.True(s01.Words[111].End <= 112 * SecondsPerWord);
    }

    [Fact]
    public async Task Every_part_gets_the_text_the_direction_the_reference_voice_and_one_seed_per_segment()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);

        await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);

        var first = _comfy.Workflows[0];
        Assert.Equal("a calm, warm documentary narrator", first["11"]!["inputs"]!["instruction"]!.GetValue<string>());
        Assert.Equal(_comfy.Uploaded.Single(), first["2"]!["inputs"]!["audio"]!.GetValue<string>());
        Assert.Equal("sdpa", first["1"]!["inputs"]!["attention"]!.GetValue<string>());   // the rest stays as exported
        var seeds = _comfy.Workflows.Select(w => w["11"]!["inputs"]!["seed"]!.GetValue<int>()).ToList();
        Assert.Equal(seeds[0], seeds[1]);   // S01's two parts
        Assert.Equal(4, seeds.Distinct().Count());
        Assert.Equal("StoryForge · Soul Coins – BG3 lore · S01 part 1 of 2", _comfy.Names[0]);
    }

    [Fact]
    public async Task Speaking_one_segment_again_leaves_the_others_as_they_are()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);
        var before = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Voice, "S02");
        var after = await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments[1].Version == 2);

        Assert.Equal([1, 2, 1, 1], after.Segments.Select(s => s.Version));
        Assert.Equal(before.Segments[0].AudioPath, after.Segments[0].AudioPath);
        Assert.NotEqual(before.Segments[1].AudioPath, after.Segments[1].AudioPath);
        Assert.True(File.Exists(before.Segments[1].AudioPath));   // v1 stays to switch back to
        Assert.Equal(10, _comfy.Texts.Count);
        Assert.Equal(StageState.Approved, after.State);
    }

    [Fact]
    public async Task When_ComfyUI_fails_the_stage_fails_with_the_reason_and_keeps_what_was_spoken()
    {
        _comfy.FailAt(3, "ComfyUI is not reachable at 127.0.0.1:8188. Start it, or check Settings → ComfyUI.");
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);

        var voice = await VoiceAsync(client, project.Id, StageState.Failed, StageState.Approved);

        Assert.Equal(StageState.Failed, voice.State);
        Assert.Equal("S02: ComfyUI is not reachable at 127.0.0.1:8188. Start it, or check Settings → ComfyUI.", voice.Error);
        var s01 = Assert.Single(voice.Segments);   // spoken before S02 failed: minutes of work, kept
        Assert.Equal(StageState.Approved, s01.State);
        var folder = Path.Combine(_projects, $"Soul Coins – BG3 lore ({project.Id.ToString("N")[..8]})", "voice");
        Assert.Equal(s01.AudioPath, Assert.Single(Directory.GetFiles(folder, "*", SearchOption.AllDirectories)));   // and no half of S02
    }

    [Fact]
    public async Task Each_segment_shows_as_soon_as_it_is_spoken_while_the_rest_are_still_to_come()
    {
        var hold = _comfy.HoldAt(5);   // S03's first part
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);
        await hold.Reached;

        var voice = await client.GetVoiceAsync(project.Id);

        Assert.Equal(StageState.Running, voice.State);
        Assert.Equal(["S01", "S02"], voice.Segments.Select(s => s.Id));
        Assert.All(voice.Segments, s => Assert.True(File.Exists(s.AudioPath)));
        Assert.Equal(120, voice.Seconds, 3);   // the total so far
        hold.Release();
        var done = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);
        Assert.Equal([1, 1, 1, 1], done.Segments.Select(s => s.Version));   // stored once, not again at the end
    }

    [Fact]
    public async Task Approving_the_script_again_after_a_failed_voice_does_not_speak_it_all_again_unasked()
    {
        _comfy.FailAt(3, "The workflow failed in ComfyUI: #11 ITLBreezeTTSVoiceDirection: CUDA out of memory.");
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client, scriptGate: true);
        await VoiceAsync(client, project.Id, StageState.Failed, StageState.Approved);
        var sent = _comfy.Workflows.Count;

        // With the Script gate: your wording goes to review, and approving it settles the script approved again.
        await client.EditSegmentAsync(project.Id, "S02", "Money of Hell", "Soul coins are the money of Hell.");
        Assert.Equal(StageState.NeedsReview, (await client.GetScriptAsync(project.Id)).State);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Script, "S02", 2);
        Assert.Equal(StageState.Approved, (await client.GetScriptAsync(project.Id)).State);

        var voice = await client.GetVoiceAsync(project.Id);
        Assert.Equal(StageState.Failed, voice.State);   // Retry is yours to press
        Assert.Equal(sent, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task Cancelling_part_way_keeps_the_segments_spoken_and_says_so()
    {
        var hold = _comfy.HoldAt(3);   // S02's first part
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);
        await hold.Reached;

        await client.CancelAsync(project.Id, PipelineStage.Voice);
        var voice = await VoiceAsync(client, project.Id, StageState.Failed, StageState.NotStarted);

        Assert.Equal(StageState.Failed, voice.State);
        Assert.Equal("Cancelled after 1 segment. Those are kept; Retry makes them all again.", voice.Error);
        Assert.Equal(["S01"], voice.Segments.Select(s => s.Id));
    }

    [Fact]
    public async Task Speaking_the_whole_voice_again_shows_the_old_segments_running_until_each_is_redone()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);
        await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);
        var hold = _comfy.HoldAt(11);   // the second run's S02, first part

        // The first run is stored a moment before the runner lets go of it, and a Regenerate in
        // between is taken for the run itself: asked again until the second run is under way.
        var reached = hold.Reached;
        await ScriptRunTests.WaitForAsync(async () =>
        {
            await client.RegenerateAsync(project.Id, PipelineStage.Voice);
            return await Task.WhenAny(reached, Task.Delay(500)) == reached;
        }, under => under);
        var voice = await client.GetVoiceAsync(project.Id);

        Assert.Equal([StageState.Approved, StageState.Running, StageState.Running, StageState.Running], voice.Segments.Select(s => s.State));
        Assert.Equal([2, 1, 1, 1], voice.Segments.Select(s => s.Version));
        hold.Release();
        var done = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);
        Assert.Equal([2, 2, 2, 2], done.Segments.Select(s => s.Version));
    }

    [Fact]
    public async Task A_workflow_that_saves_wav_files_is_converted_and_joined_like_any_other()
    {
        _comfy.Extension = ".wav";
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);

        var voice = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);

        Assert.Null(voice.Error);
        Assert.All(voice.Segments, s => Assert.Equal(60, s.Seconds, 3));   // each part once
    }

    [Fact]
    public async Task A_workflow_saved_in_the_editor_format_fails_and_says_how_to_export_it()
    {
        File.WriteAllText(Path.Combine(_templates, "editor.json"), """{ "id": "x", "nodes": [], "links": [], "version": 0.4 }""");
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client, workflow: "editor.json");

        var voice = await VoiceAsync(client, project.Id, StageState.Failed, StageState.Approved);

        Assert.Contains("Export (API)", voice.Error);
        Assert.Empty(_comfy.Texts);
    }

    [Fact]
    public async Task With_the_voice_gate_each_segment_waits_for_review_and_approving_them_all_approves_the_voice()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client, voiceGate: true);

        var voice = await VoiceAsync(client, project.Id, StageState.NeedsReview, StageState.Failed);
        Assert.All(voice.Segments, s => Assert.Equal(StageState.NeedsReview, s.State));

        await client.ApproveSegmentAsync(project.Id, PipelineStage.Voice, "S01", 1);
        Assert.Equal(StageState.NeedsReview, (await client.GetVoiceAsync(project.Id)).State);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Voice);

        var approved = await client.GetVoiceAsync(project.Id);
        Assert.Equal(StageState.Approved, approved.State);
        Assert.All(approved.Segments, s => Assert.Equal((StageState.Approved, (int?)1), (s.State, s.ApprovedVersion)));
    }

    [Fact]
    public async Task A_voice_switched_back_to_an_older_version_plays_that_file()
    {
        var client = await StartAsync();
        var project = await ScriptApprovedAsync(client);
        var before = await VoiceAsync(client, project.Id, StageState.Approved, StageState.Failed);
        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Voice, "S03");
        await ScriptRunTests.WaitForAsync(() => client.GetVoiceAsync(project.Id), v => v.Segments[2].Version == 2);

        await client.SelectSegmentVersionAsync(project.Id, PipelineStage.Voice, "S03", 1);

        var back = await client.GetVoiceAsync(project.Id);
        Assert.Equal((1, before.Segments[2].AudioPath), (back.Segments[2].Version, back.Segments[2].AudioPath));
    }

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>ComfyUI that "speaks" by writing the text it got as the file; records what it was sent.</summary>
    private sealed class FakeComfy : IComfyUi
    {
        private int _failAt;
        private string _reason = "";

        public List<JsonObject> Workflows { get; } = [];

        public List<string> Texts { get; } = [];

        public List<string> Uploaded { get; } = [];

        public List<string> Names { get; } = [];

        /// <summary>What the "saved" file ends in, as the workflow's save node chose.</summary>
        public string Extension { get; set; } = ".mp3";

        private int _holdAt;
        private Hold? _hold;

        /// <summary>The <paramref name="run"/>th workflow (from 1) fails with <paramref name="reason"/>.</summary>
        public void FailAt(int run, string reason) => (_failAt, _reason) = (run, reason);

        /// <summary>The <paramref name="run"/>th workflow (from 1) waits until released or cancelled.</summary>
        public Hold HoldAt(int run)
        {
            _holdAt = run;
            return _hold = new Hold();
        }

        public sealed class Hold
        {
            private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(15));

            public void Release() => _released.TrySetResult();

            public async Task WaitAsync(CancellationToken cancellationToken)
            {
                _reached.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<string> UploadAsync(string path, CancellationToken cancellationToken)
        {
            var name = "storyforge-" + Path.GetFileName(path);
            lock (Uploaded)
            {
                Uploaded.Add(name);
            }
            return Task.FromResult(name);
        }

        public async Task<IReadOnlyList<ComfyFile>> RunAsync(JsonObject workflow, string name, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Hold? hold;
            string text;
            lock (Workflows)
            {
                Workflows.Add(workflow);
                Names.Add(name);
                if (Workflows.Count == _failAt)
                {
                    throw new StageFailedException(_reason);
                }
                hold = Workflows.Count == _holdAt ? _hold : null;
                text = workflow["11"]!["inputs"]!["text"]!.GetValue<string>();
                Texts.Add(text);
            }
            progress.Report("sent to ComfyUI");
            if (hold is not null)
            {
                await hold.WaitAsync(cancellationToken);
            }
            return [new ComfyFile("audio", "BreezeTTS_00001_" + Extension, Encoding.UTF8.GetBytes(text))];
        }
    }

    /// <summary>ffmpeg that turns the "spoken" text into silence of 0.4 s a word (1 kHz, 16-bit, mono).</summary>
    private sealed class FakeConverter : IAudioConverter
    {
        public async Task ToWavAsync(string source, string target, CancellationToken cancellationToken)
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new StageFailedException("ffmpeg could not read the audio ComfyUI gave: Output same as Input.");   // as ffmpeg refuses it
            }
            var words = Words(await File.ReadAllTextAsync(source, cancellationToken));
            await File.WriteAllBytesAsync(target, WavTests.Silence(words * SecondsPerWord), cancellationToken);
        }
    }
}
