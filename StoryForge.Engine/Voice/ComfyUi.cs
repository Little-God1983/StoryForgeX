using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Voice;

/// <summary>A file a workflow saved, downloaded: e.g. Kind "audio", Name "BreezeTTS_00012_.mp3".</summary>
internal sealed record ComfyFile(string Kind, string Name, byte[] Content);

/// <summary>
/// ComfyUI as the stages use it: one workflow at a time, submitted, followed to the end, and its
/// saved files downloaded. The image and video stages reuse it.
/// </summary>
internal interface IComfyUi
{
    /// <summary>Puts a local file into ComfyUI's input folder; returns the name a Load node takes.</summary>
    /// <exception cref="StageFailedException">ComfyUI is not reachable or refused the file.</exception>
    Task<string> UploadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Runs an API-format workflow and returns the files its save nodes wrote.</summary>
    /// <param name="name">What ComfyUI's queue shows for it, e.g. "StoryForge · Soul Coins · S03 part 1 of 2".</param>
    /// <exception cref="StageFailedException">ComfyUI is not reachable, refused the workflow, or it failed.</exception>
    Task<IReadOnlyList<ComfyFile>> RunAsync(JsonObject workflow, string name, IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>ComfyUI over its HTTP API, at the host and port in Settings.</summary>
internal sealed class ComfyUiClient(HttpMessageHandler handler, SettingsStore settings, TimeProvider clock) : IComfyUi
{
    /// <summary>How often a running workflow is asked about.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest one workflow may take, model loading included.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(20);

    private static readonly string ClientId = Guid.NewGuid().ToString("N");

    public async Task<string> UploadAsync(string path, CancellationToken cancellationToken)
    {
        var (http, address) = await ConnectAsync(cancellationToken);
        using (http)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            // Named by content: the same reference uploads once and never clashes with your own files.
            var name = "storyforge-" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + Path.GetExtension(path).ToLowerInvariant();
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(bytes), "image", name },
                { new StringContent("input"), "type" },
                { new StringContent("true"), "overwrite" },
            };
            using var response = await SendAsync(http, address, () => http.PostAsync("upload/image", form, cancellationToken));
            if (!response.IsSuccessStatusCode)
            {
                throw new StageFailedException($"ComfyUI refused the reference file {Path.GetFileName(path)} (HTTP {(int)response.StatusCode}).");
            }
            var answer = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var subfolder = answer?["subfolder"]?.GetValue<string>() ?? "";
            var stored = answer?["name"]?.GetValue<string>() ?? name;
            return subfolder.Length == 0 ? stored : $"{subfolder}/{stored}";
        }
    }

    public async Task<IReadOnlyList<ComfyFile>> RunAsync(JsonObject workflow, string name, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var (http, address) = await ConnectAsync(cancellationToken);
        using (http)
        {
            var promptId = await SubmitAsync(http, address, workflow, name, cancellationToken);
            progress.Report("sent to ComfyUI");
            try
            {
                var outputs = await WaitAsync(http, address, promptId, cancellationToken);
                return await DownloadAsync(http, address, outputs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopAsync(http, promptId);
                throw;
            }
        }
    }

    private async Task<string> SubmitAsync(HttpClient http, string address, JsonObject workflow, string name, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["prompt"] = workflow.DeepClone(),
            ["client_id"] = ClientId,
            // The shape of what ComfyUI's editor sends with every prompt; custom nodes read it
            // unguarded. The queue manager's worker dies on a prompt without a workflow name (and
            // ComfyUI runs nothing more until it is restarted); Show Text and others look for their
            // node in "nodes". An empty graph satisfies both.
            ["extra_data"] = new JsonObject
            {
                ["extra_pnginfo"] = new JsonObject
                {
                    ["workflow"] = new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString(),
                        ["workflow_name"] = name,
                        ["nodes"] = new JsonArray(),
                        ["links"] = new JsonArray(),
                    },
                },
            },
        };
        using var response = await SendAsync(http, address, () => http.PostAsJsonAsync("prompt", body, cancellationToken));
        var answer = await ReadAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new StageFailedException($"ComfyUI refused the workflow: {Refusal(answer)}");
        }
        return answer?["prompt_id"]?.GetValue<string>()
            ?? throw new StageFailedException("ComfyUI accepted the workflow but gave no prompt id.");
    }

    /// <summary>Asks after the prompt until its history has it; returns its outputs.</summary>
    private async Task<JsonObject> WaitAsync(HttpClient http, string address, string promptId, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var response = await SendAsync(http, address, () => http.GetAsync($"history/{promptId}", cancellationToken)))
            {
                if (response.IsSuccessStatusCode && (await ReadAsync(response, cancellationToken))?[promptId] is JsonObject entry)
                {
                    var status = entry["status"] as JsonObject;
                    if (status?["status_str"]?.GetValue<string>() == "error")
                    {
                        throw new StageFailedException($"The workflow failed in ComfyUI: {ExecutionError(status)}");
                    }
                    if (status?["completed"]?.GetValue<bool>() != false)
                    {
                        return entry["outputs"] as JsonObject ?? [];
                    }
                }
            }
            if (clock.GetUtcNow() - started > Deadline)
            {
                await StopAsync(http, promptId);
                throw new StageFailedException($"ComfyUI did not finish within {Deadline.TotalMinutes:0} minutes.");
            }
            await Task.Delay(PollInterval, clock, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<ComfyFile>> DownloadAsync(HttpClient http, string address, JsonObject outputs, CancellationToken cancellationToken)
    {
        var files = new List<ComfyFile>();
        foreach (var (_, node) in outputs)
        {
            foreach (var (kind, list) in node as JsonObject ?? [])
            {
                if (list is not JsonArray items)
                {
                    continue;
                }
                foreach (var item in items.OfType<JsonObject>())
                {
                    // Saved files only: a preview node writes to "temp", and a text node lists strings.
                    if (item["filename"]?.GetValue<string>() is not { } name || item["type"]?.GetValue<string>() != "output")
                    {
                        continue;
                    }
                    var subfolder = item["subfolder"]?.GetValue<string>() ?? "";
                    var query = $"view?filename={Uri.EscapeDataString(name)}&subfolder={Uri.EscapeDataString(subfolder)}&type=output";
                    using var response = await SendAsync(http, address, () => http.GetAsync(query, cancellationToken));
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new StageFailedException($"ComfyUI saved {name} but would not hand it over (HTTP {(int)response.StatusCode}).");
                    }
                    files.Add(new ComfyFile(kind, name, await response.Content.ReadAsByteArrayAsync(cancellationToken)));
                }
            }
        }
        return files;
    }

    /// <summary>Takes the prompt out of the queue, or stops it if it runs. Best effort: it is being dropped anyway.</summary>
    private static async Task StopAsync(HttpClient http, string promptId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var deleted = await http.PostAsJsonAsync("queue", new JsonObject { ["delete"] = new JsonArray(promptId) }, timeout.Token);
            using var interrupted = await http.PostAsJsonAsync("interrupt", new JsonObject { ["prompt_id"] = promptId }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // ComfyUI went away meanwhile; nothing is left to stop.
        }
    }

    private async Task<(HttpClient Http, string Address)> ConnectAsync(CancellationToken cancellationToken)
    {
        var comfy = (await settings.LoadAsync(cancellationToken)).ComfyUi;
        if (string.IsNullOrWhiteSpace(comfy.Host))
        {
            throw new StageFailedException("No ComfyUI host is set. Set it in Settings → ComfyUI.");
        }
        var address = $"{comfy.Host.Trim()}:{comfy.Port}";
        if (!Uri.TryCreate($"http://{address}/", UriKind.Absolute, out var root))
        {
            throw new StageFailedException($"'{comfy.Host}' is not a valid ComfyUI host. Check Settings → ComfyUI.");
        }
        return (new HttpClient(handler, disposeHandler: false) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(60) }, address);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string address, Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException)
        {
            throw new StageFailedException($"ComfyUI is not reachable at {address}. Start it, or check Settings → ComfyUI.");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new StageFailedException($"ComfyUI at {address} did not answer within {http.Timeout.TotalSeconds:0} s.");
        }
    }

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Prompt outputs failed validation: #11 ITLBreezeTTSVoiceDirection: text – Required input is missing".</summary>
    private static string Refusal(JsonObject? answer)
    {
        var error = answer?["error"] as JsonObject;
        var message = error?["message"]?.GetValue<string>() ?? "no reason given";
        var nodes = (answer?["node_errors"] as JsonObject ?? [])
            .Select(n =>
            {
                var type = n.Value?["class_type"]?.GetValue<string>() ?? "";
                var first = (n.Value?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var what = first is null ? "" : $"{first["details"]?.GetValue<string>()} – {first["message"]?.GetValue<string>()}".Trim(' ', '–');
                return $"#{n.Key} {type}: {what}".TrimEnd(' ', ':');
            })
            .ToList();
        return nodes.Count == 0 ? message : $"{message}: {string.Join("; ", nodes)}";
    }

    /// <summary>"#11 ITLBreezeTTSVoiceDirection: CUDA out of memory".</summary>
    private static string ExecutionError(JsonObject status)
    {
        foreach (var message in (status["messages"] as JsonArray ?? []).OfType<JsonArray>())
        {
            if (message.Count == 2 && message[0]?.GetValue<string>() == "execution_error" && message[1] is JsonObject detail)
            {
                var node = detail["node_id"]?.ToString();
                var type = detail["node_type"]?.GetValue<string>();
                var text = detail["exception_message"]?.GetValue<string>()?.Trim() ?? "no message";
                return node is null ? text : $"#{node} {type}: {text}";
            }
        }
        return "no message";
    }
}
