using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

public sealed class WavTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public WavTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A WAV of silence: 1 kHz, 16-bit, mono, so a second is 2000 bytes.</summary>
    internal static byte[] Silence(double seconds, bool withListChunk = false)
    {
        var data = (int)Math.Round(seconds * 2000) & ~1;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var list = withListChunk ? "INFOISFT"u8.ToArray() : [];
        writer.Write("RIFF"u8);
        writer.Write(4 + 24 + (withListChunk ? 8 + list.Length : 0) + 8 + data);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // mono
        writer.Write(1000);          // sample rate
        writer.Write(2000);          // byte rate
        writer.Write((short)2);      // block align
        writer.Write((short)16);     // bits
        if (withListChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(list.Length);
            writer.Write(list);
        }
        writer.Write("data"u8);
        writer.Write(data);
        writer.Write(new byte[data]);
        return stream.ToArray();
    }

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_file_is_as_long_as_its_audio_says()
    {
        Assert.Equal(2.5, Wav.Seconds(File("a.wav", Silence(2.5))), 3);
    }

    [Fact]
    public void Chunks_before_the_audio_are_skipped()
    {
        Assert.Equal(1.5, Wav.Seconds(File("a.wav", Silence(1.5, withListChunk: true))), 3);
    }

    [Fact]
    public void Joined_parts_are_as_long_as_the_parts_together()
    {
        var target = Path.Combine(_folder, "joined.wav");

        Wav.Join([File("a.wav", Silence(44.8)), File("b.wav", Silence(15.2, withListChunk: true))], target);

        Assert.Equal(60, Wav.Seconds(target), 3);
    }

    [Fact]
    public void A_file_that_is_no_wav_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Wav.Seconds(File("a.mp3", "ID3 not a wave"u8.ToArray())));
    }
}

public sealed class NarrationPartsTests
{
    private static string Sentence(int words, string end = ".") => string.Join(' ', Enumerable.Repeat("word", words)) + end;

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void A_short_narration_is_one_part()
    {
        Assert.Equal(["Soul coins scream. They hold one soul."], NarrationParts.Split("Soul coins scream.  They hold one soul.", "English"));
    }

    [Fact]
    public void Parts_end_at_sentences_and_stay_within_45_seconds()
    {
        // Ten sentences of 20 words: 200 words, 80 s in English (150 a minute; 45 s is 112 words).
        var narration = string.Join(' ', Enumerable.Range(0, 10).Select(_ => Sentence(20)));

        var parts = NarrationParts.Split(narration, "English");

        Assert.Equal([100, 100], parts.Select(Words));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(narration, string.Join(' ', parts));
    }

    [Fact]
    public void A_sentence_too_long_for_one_part_is_cut_at_its_commas()
    {
        var narration = string.Join(' ', Enumerable.Range(0, 4).Select(_ => Sentence(50, ","))).TrimEnd(',') + ".";

        var parts = NarrationParts.Split(narration, "English");

        Assert.Equal([100, 100], parts.Select(Words));
        Assert.EndsWith(",", parts[0]);
    }

    [Fact]
    public void Without_any_pause_the_words_are_cut_into_runs_that_fit()
    {
        Assert.Equal([112, 112, 76], NarrationParts.Split(Sentence(300, ""), "English").Select(Words));
    }

    [Fact]
    public void A_slower_language_has_shorter_parts()
    {
        // German: 130 words a minute, so 45 s is 97 words.
        Assert.Equal([97, 3], NarrationParts.Split(Sentence(100, ""), "German").Select(Words));
    }

    [Fact]
    public void Quotes_after_the_full_stop_stay_with_their_sentence()
    {
        var narration = Sentence(60) + "\" " + Sentence(60);

        var parts = NarrationParts.Split(narration, "English");

        Assert.Equal(2, parts.Count);
        Assert.EndsWith(".\"", parts[0]);
    }
}

public sealed class WordTimingsTests
{
    [Fact]
    public void Words_fill_the_part_between_its_edges_in_order()
    {
        var words = WordTimings.Estimate([new VoicePart("Soul coins scream.", 3)]);

        Assert.Equal(["Soul", "coins", "scream."], words.Select(w => w.Text));
        Assert.Equal(0.15, words[0].Start, 3);
        Assert.Equal(2.85, words[^1].End, 3);
        Assert.All(words.Zip(words.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
        // A longer word takes longer.
        Assert.True(words[1].End - words[1].Start > words[0].End - words[0].Start);
    }

    [Fact]
    public void The_words_of_a_part_start_after_the_parts_before_it()
    {
        var words = WordTimings.Estimate([new VoicePart("One two.", 2), new VoicePart("Three four.", 3)]);

        Assert.Equal(2.15, words[2].Start, 3);
        Assert.Equal(4.85, words[3].End, 3);
    }

    [Fact]
    public void A_comma_leaves_a_pause_and_a_full_stop_a_longer_one()
    {
        var words = WordTimings.Estimate([new VoicePart("aaaa, bbbb cccc. dddd", 10)]);

        var comma = words[1].Start - words[0].End;
        var none = words[2].Start - words[1].End;
        var stop = words[3].Start - words[2].End;
        Assert.Equal(0, none, 6);
        Assert.True(comma > 0);
        Assert.True(stop > comma);
    }

    [Fact]
    public void A_very_short_part_still_gets_its_words_in()
    {
        var words = WordTimings.Estimate([new VoicePart("Yes.", 0.2)]);

        Assert.True(words[0].Start >= 0 && words[0].End <= 0.2);
        Assert.True(words[0].End > words[0].Start);
    }
}

public sealed class WorkflowTemplateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public WorkflowTemplateTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static JsonObject Api() => (JsonObject)JsonNode.Parse("""
        { "11": { "class_type": "ITLBreezeTTSVoiceDirection", "inputs": { "text": "", "seed": 1, "instruction": "from the workflow" } } }
        """)!;

    [Fact]
    public void Values_go_into_the_nodes_the_profile_names()
    {
        var applied = WorkflowTemplate.Apply(Api(), [new("text", "#11.text"), new("seed", "11.seed")],
            new Dictionary<string, JsonNode> { ["text"] = "Soul coins scream.", ["seed"] = 42 });

        Assert.Equal("Soul coins scream.", applied["11"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(42, applied["11"]!["inputs"]!["seed"]!.GetValue<int>());
    }

    [Fact]
    public void A_key_the_profile_does_not_map_keeps_the_workflows_own_value()
    {
        var workflow = Api();

        var applied = WorkflowTemplate.Apply(workflow, [new("text", "#11.text"), new("instruction", "")],
            new Dictionary<string, JsonNode> { ["text"] = "x", ["instruction"] = "calm" });

        Assert.Equal("from the workflow", applied["11"]!["inputs"]!["instruction"]!.GetValue<string>());
        Assert.Equal("", workflow["11"]!["inputs"]!["text"]!.GetValue<string>());   // the template itself is untouched
    }

    [Theory]
    [InlineData("#12.text", "the workflow has no node 12")]
    [InlineData("#11", "write it as #node.input")]
    public void A_mapping_that_does_not_fit_the_workflow_says_why(string node, string expected)
    {
        var ex = Assert.Throws<StageFailedException>(() =>
            WorkflowTemplate.Apply(Api(), [new("text", node)], new Dictionary<string, JsonNode> { ["text"] = "x" }));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_workflow_saved_by_the_editor_is_refused_with_how_to_export_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "ui.json"), """{ "nodes": [ { "id": 11 } ], "links": [] }""");

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => WorkflowTemplate.LoadAsync(_folder, "ui.json", CancellationToken.None));

        Assert.Contains("Workflow → Export (API)", ex.Message);
    }

    [Theory]
    [InlineData("", "voice.json", "No ComfyUI templates folder is set")]
    [InlineData("<folder>", "", "has no workflow")]
    [InlineData("<folder>", "missing.json", "is not in the templates folder")]
    public async Task A_workflow_that_cannot_be_found_says_where_to_set_it(string folder, string file, string expected)
    {
        var ex = await Assert.ThrowsAsync<StageFailedException>(() =>
            WorkflowTemplate.LoadAsync(folder.Replace("<folder>", _folder), file, CancellationToken.None));

        Assert.Contains(expected, ex.Message);
    }
}

public sealed class ComfyUiClientTests : IDisposable
{
    private const string Root = "http://127.0.0.1:8188/";

    private readonly EngineTestHost _engine = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose() => _engine.Dispose();

    private async Task<ComfyUiClient> ClientAsync()
    {
        var host = await _engine.StartAsync(services => services.Replace(ServiceDescriptor.Singleton<HttpMessageHandler>(_http)));
        return ActivatorUtilities.CreateInstance<ComfyUiClient>(host.Services);
    }

    private static JsonObject Workflow() => (JsonObject)JsonNode.Parse("""{ "11": { "class_type": "Tts", "inputs": { "text": "Soul coins scream." } } }""")!;

    private static readonly Progress<string> Quiet = new();

    private const string Done = """
        { "p1": { "status": { "status_str": "success", "completed": true, "messages": [] },
                  "outputs": { "4": { "audio": [ { "filename": "BreezeTTS_00001_.mp3", "subfolder": "audio", "type": "output" } ] },
                               "9": { "audio": [ { "filename": "preview.flac", "subfolder": "", "type": "temp" } ] },
                               "17": { "text": [ "I'm an American music host." ] } } } }
        """;

    [Fact]
    public async Task A_workflow_is_submitted_followed_to_the_end_and_its_saved_audio_downloaded()
    {
        string? sent = null;
        var asked = 0;
        ComfyUiClient.PollInterval = TimeSpan.FromMilliseconds(5);
        _http.Answer(Root + "prompt", request =>
            {
                sent = request.Content!.ReadAsStringAsync().Result;
                return Json("""{ "prompt_id": "p1", "number": 3 }""");
            })
            .Answer(Root + "history/p1", _ => Json(++asked < 3 ? "{}" : Done))
            .Answer(Root + "view?filename=BreezeTTS_00001_.mp3&subfolder=audio&type=output", _ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("mp3!"u8.ToArray()) });
        var client = await ClientAsync();

        var files = await client.RunAsync(Workflow(), "StoryForge · test", Quiet, CancellationToken.None);

        var file = Assert.Single(files);   // the preview (temp) and the transcript are no saved files
        Assert.Equal(("audio", "BreezeTTS_00001_.mp3", "mp3!"), (file.Kind, file.Name, Encoding.UTF8.GetString(file.Content)));
        var body = JsonNode.Parse(sent!)!;
        Assert.Equal("Soul coins scream.", body["prompt"]!["11"]!["inputs"]!["text"]!.GetValue<string>());
        // As ComfyUI's editor sends it: a queue extension that reads the name unguarded must find one.
        Assert.Equal("StoryForge · test", body["extra_data"]!["extra_pnginfo"]!["workflow"]!["workflow_name"]!.GetValue<string>());
        Assert.NotNull(body["extra_data"]!["extra_pnginfo"]!["workflow"]!["id"]);
        Assert.IsType<JsonArray>(body["extra_data"]!["extra_pnginfo"]!["workflow"]!["nodes"]);   // Show Text looks for itself there
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task A_refused_workflow_says_which_node_and_why()
    {
        _http.Answer(Root + "prompt", HttpStatusCode.BadRequest, """
            { "error": { "type": "prompt_outputs_failed_validation", "message": "Prompt outputs failed validation", "details": "" },
              "node_errors": { "11": { "errors": [ { "type": "required_input_missing", "message": "Required input is missing", "details": "text" } ],
                                       "class_type": "ITLBreezeTTSVoiceDirection" } } }
            """);
        var client = await ClientAsync();

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => client.RunAsync(Workflow(), "StoryForge · test", Quiet, CancellationToken.None));

        Assert.Equal("ComfyUI refused the workflow: Prompt outputs failed validation: #11 ITLBreezeTTSVoiceDirection: text – Required input is missing", ex.Message);
    }

    [Fact]
    public async Task A_workflow_that_fails_while_running_says_which_node_and_why()
    {
        _http.Answer(Root + "prompt", HttpStatusCode.OK, """{ "prompt_id": "p1" }""")
            .Answer(Root + "history/p1", HttpStatusCode.OK, """
                { "p1": { "status": { "status_str": "error", "completed": false, "messages": [
                    [ "execution_start", { "prompt_id": "p1" } ],
                    [ "execution_error", { "node_id": "11", "node_type": "ITLBreezeTTSVoiceDirection", "exception_message": "CUDA out of memory.\n" } ] ] },
                  "outputs": {} } }
                """);
        var client = await ClientAsync();

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => client.RunAsync(Workflow(), "StoryForge · test", Quiet, CancellationToken.None));

        Assert.Equal("The workflow failed in ComfyUI: #11 ITLBreezeTTSVoiceDirection: CUDA out of memory.", ex.Message);
    }

    [Fact]
    public async Task ComfyUI_that_is_not_running_is_said_so_in_plain_words()
    {
        var client = await ClientAsync();

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => client.RunAsync(Workflow(), "StoryForge · test", Quiet, CancellationToken.None));

        Assert.Equal("ComfyUI is not reachable at 127.0.0.1:8188. Start it, or check Settings → ComfyUI.", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_run_is_taken_out_of_ComfyUIs_queue()
    {
        using var cancel = new CancellationTokenSource();
        ComfyUiClient.PollInterval = TimeSpan.FromMilliseconds(5);
        _http.Answer(Root + "prompt", HttpStatusCode.OK, """{ "prompt_id": "p1" }""")
            .Answer(Root + "history/p1", _ =>
            {
                cancel.Cancel();
                return Json("{}");
            })
            .Answer(Root + "queue", HttpStatusCode.OK)
            .Answer(Root + "interrupt", HttpStatusCode.OK);
        var client = await ClientAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunAsync(Workflow(), "StoryForge · test", Quiet, cancel.Token));

        Assert.Contains(_http.Requests, r => r.RequestUri!.ToString() == Root + "queue");
        Assert.Contains(_http.Requests, r => r.RequestUri!.ToString() == Root + "interrupt");
    }

    [Fact]
    public async Task A_reference_file_is_uploaded_under_a_name_from_its_content()
    {
        var reference = Path.Combine(_engine.DataDirectory, "amira.MP3");
        Directory.CreateDirectory(_engine.DataDirectory);
        await File.WriteAllTextAsync(reference, "a voice");
        string? body = null;
        _http.Answer(Root + "upload/image", request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("""{ "name": "storyforge-0123456789abcdef.mp3", "subfolder": "", "type": "input" }""");
        });
        var client = await ClientAsync();

        var name = await client.UploadAsync(reference, CancellationToken.None);

        Assert.Equal("storyforge-0123456789abcdef.mp3", name);
        Assert.Matches("filename=storyforge-[0-9a-f]{16}\\.mp3", body);
        Assert.Contains("a voice", body);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class FfmpegAudioConverterTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly FakeProcessRunner _processes = new();

    public void Dispose() => _engine.Dispose();

    private async Task<FfmpegAudioConverter> ConverterAsync()
    {
        var host = await _engine.StartAsync(services => services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(_processes)));
        return ActivatorUtilities.CreateInstance<FfmpegAudioConverter>(host.Services);
    }

    [Fact]
    public async Task Without_ffmpeg_the_stage_says_where_to_set_it()
    {
        var converter = await ConverterAsync();

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => converter.ToWavAsync("a.mp3", "a.wav", CancellationToken.None));

        Assert.Equal("ffmpeg was not found ('ffmpeg'). Install it, or set its path in Settings → FFmpeg.", ex.Message);
    }

    [Fact]
    public async Task Audio_ffmpeg_cannot_read_fails_with_its_last_word()
    {
        _processes.Answer("ffmpeg", _ => new ProcessRunResult(1, "", "Input #0\r\na.mp3: Invalid data found when processing input\r\n"));
        var converter = await ConverterAsync();

        var ex = await Assert.ThrowsAsync<StageFailedException>(() => converter.ToWavAsync("a.mp3", "a.wav", CancellationToken.None));

        Assert.Equal("ffmpeg could not read the audio ComfyUI gave: a.mp3: Invalid data found when processing input.", ex.Message);
    }

    [Fact]
    public async Task Every_part_is_made_mono_48_kHz_16_bit_so_the_parts_join()
    {
        var target = Path.Combine(_engine.DataDirectory, "a.wav");
        _processes.Answer("ffmpeg", _ =>
        {
            File.WriteAllBytes(target, WavTests.Silence(1));
            return new ProcessRunResult(0, "", "");
        });
        var converter = await ConverterAsync();

        await converter.ToWavAsync("C:\\in put\\a.mp3", target, CancellationToken.None);

        Assert.Equal($"-y -hide_banner -loglevel error -i \"C:\\in put\\a.mp3\" -ac 1 -ar 48000 -c:a pcm_s16le \"{target}\"", _processes.Calls.Single().Arguments);
    }
}
