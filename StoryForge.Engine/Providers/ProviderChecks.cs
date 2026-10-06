using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StoryForge.Client;
using StoryForge.Engine.Secrets;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Providers;

/// <summary>
/// Asks every provider whether it works, right now: CLIs answer their version flag, HTTP
/// providers answer a cheap read-only endpoint. All checks run in parallel.
/// </summary>
internal sealed class ProviderChecks(
    SettingsStore settingsStore,
    IProcessRunner processes,
    HttpMessageHandler httpHandler,
    ISecretStore secrets)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<ProviderStatus>> CheckAllAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var checks = await Task.WhenAll(
            CheckCliAsync(ProviderId.ClaudeCli, "Claude CLI", settings.ClaudeCli.Executable, "--version", cancellationToken),
            CheckLmStudioAsync(settings.LmStudio, cancellationToken),
            CheckComfyUiAsync(settings.ComfyUi, cancellationToken),
            CheckCliAsync(ProviderId.Ffmpeg, "FFmpeg", settings.Ffmpeg.Executable, "-version", cancellationToken));

        return
        [
            .. checks,
            new ProviderStatus(ProviderId.ContentAutomatorX, "CAX", ProviderState.NotSetUp),
            new ProviderStatus(ProviderId.DavinciResolve, "Resolve", ProviderState.NotSetUp),
        ];
    }

    private async Task<ProviderStatus> CheckCliAsync(
        ProviderId id, string name, string executable, string versionFlag, CancellationToken cancellationToken)
    {
        ProviderStatus Status(ProviderState state, string? detail = null) => new(id, name, state, detail);

        if (string.IsNullOrWhiteSpace(executable))
        {
            return Status(ProviderState.NotSetUp, "no executable set");
        }
        try
        {
            var result = await processes.RunAsync(executable.Trim(), versionFlag, ProcessTimeout, cancellationToken);
            return result.ExitCode == 0
                ? Status(ProviderState.Ok, VersionLine(result.StandardOutput))
                : Status(ProviderState.Error, $"exited with code {result.ExitCode}: {FirstLine(result.StandardError)}");
        }
        catch (ExecutableNotFoundException)
        {
            return Status(ProviderState.Error, $"not found: {executable.Trim()}");
        }
        catch (TimeoutException)
        {
            return Status(ProviderState.Error, $"no answer within {ProcessTimeout.TotalSeconds:0} s");
        }
    }

    private async Task<ProviderStatus> CheckComfyUiAsync(ComfyUiSettings comfy, CancellationToken cancellationToken)
    {
        ProviderStatus Status(ProviderState state, string? detail = null) => new(ProviderId.ComfyUi, "ComfyUI", state, detail);

        if (string.IsNullOrWhiteSpace(comfy.Host))
        {
            return Status(ProviderState.NotSetUp, "no host set");
        }
        var address = $"{comfy.Host.Trim()}:{comfy.Port}";
        if (!Uri.TryCreate($"http://{address}/system_stats", UriKind.Absolute, out var url))
        {
            return Status(ProviderState.Error, $"not a valid host: {comfy.Host}");
        }

        var (response, failure) = await GetAsync(url, token: null, cancellationToken);
        using (response)
        {
            if (response is null)
            {
                return Status(ProviderState.Off, failure == Failure.Timeout
                    ? $"not reachable at {address} (no answer within {HttpTimeout.TotalSeconds:0} s)"
                    : $"not reachable at {address}");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Status(ProviderState.Error, $"HTTP {(int)response.StatusCode} from /system_stats");
            }
            var version = await ReadAsync(response, json =>
                json.TryGetProperty("system", out var system) && system.TryGetProperty("comfyui_version", out var v) ? v.GetString() : null,
                cancellationToken);
            return Status(ProviderState.Ok, version is null ? "reachable" : $"ComfyUI {version}");
        }
    }

    private async Task<ProviderStatus> CheckLmStudioAsync(LmStudioSettings lmStudio, CancellationToken cancellationToken)
    {
        ProviderStatus Status(ProviderState state, string? detail = null) => new(ProviderId.LmStudio, "LM Studio", state, detail);

        var baseUrl = lmStudio.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            return Status(ProviderState.NotSetUp, "no base URL set");
        }
        if (!Uri.TryCreate(baseUrl + "/models", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return Status(ProviderState.Error, $"not a valid URL: {lmStudio.BaseUrl}");
        }

        var token = secrets.Read(InProcessStoryForgeClient.SecretName(SecretKey.LmStudioApiToken));
        var (response, failure) = await GetAsync(url, token, cancellationToken);
        using (response)
        {
            if (response is null)
            {
                return Status(ProviderState.Off, failure == Failure.Timeout
                    ? $"not reachable at {baseUrl} (no answer within {HttpTimeout.TotalSeconds:0} s)"
                    : $"not reachable at {baseUrl}");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Status(ProviderState.Error, "API token rejected");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Status(ProviderState.Error, $"HTTP {(int)response.StatusCode} from /models");
            }
            var models = await ReadAsync(response, json =>
                json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList()
                    : null,
                cancellationToken) ?? [];

            var model = lmStudio.Model.Trim();
            return model.Length > 0 && !models.Contains(model)
                ? Status(ProviderState.Error, $"model '{model}' is not available")
                : Status(ProviderState.Ok, models.Count == 1 ? "1 model" : $"{models.Count} models");
        }
    }

    private enum Failure
    {
        Unreachable,
        Timeout,
    }

    private async Task<(HttpResponseMessage? Response, Failure Failure)> GetAsync(
        Uri url, string? token, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(httpHandler, disposeHandler: false) { Timeout = HttpTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        try
        {
            return (await http.SendAsync(request, cancellationToken), Failure.Unreachable);
        }
        catch (HttpRequestException)
        {
            return (null, Failure.Unreachable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure.Timeout);
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, Func<JsonElement, T?> read, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.ValueKind == JsonValueKind.Object ? read(json.RootElement) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    // "ffmpeg version 6.1.1 Copyright (c) …" → "ffmpeg version 6.1.1"
    private static string VersionLine(string output)
    {
        var line = FirstLine(output);
        var copyright = line.IndexOf(" Copyright", StringComparison.Ordinal);
        return copyright > 0 ? line[..copyright] : line;
    }
}
