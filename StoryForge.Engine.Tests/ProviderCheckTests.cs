using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using StoryForge.Client;
using StoryForge.Engine.Providers;

namespace StoryForge.Engine.Tests;

public sealed class ProviderCheckTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose() => _engine.Dispose();

    private Task<IStoryForgeClient> ClientAsync() => _engine.StartClientAsync(services =>
    {
        services.AddSingleton<IProcessRunner>(_processes);
        services.AddSingleton<HttpMessageHandler>(_http);
    });

    private static async Task<ProviderStatus> StatusOf(IStoryForgeClient client, ProviderId id) =>
        (await client.GetProviderStatusesAsync()).Single(s => s.Id == id);

    private static ProcessRunResult Ok(string output) => new(0, output, "");

    [Fact]
    public async Task Every_provider_is_reported_once_in_settings_order()
    {
        var client = await ClientAsync();

        var statuses = await client.GetProviderStatusesAsync();

        Assert.Equal(Enum.GetValues<ProviderId>(), statuses.Select(s => s.Id));
        Assert.Equal(["Claude CLI", "LM Studio", "ComfyUI", "FFmpeg", "CAX", "Resolve"], statuses.Select(s => s.Name));
    }

    [Fact]
    public async Task Content_automator_and_resolve_are_not_set_up_until_their_cards_exist()
    {
        var client = await ClientAsync();

        Assert.Equal(ProviderState.NotSetUp, (await StatusOf(client, ProviderId.ContentAutomatorX)).State);
        Assert.Equal(ProviderState.NotSetUp, (await StatusOf(client, ProviderId.DavinciResolve)).State);
    }

    // --- Claude CLI and FFmpeg: run the executable with its version flag ---

    [Fact]
    public async Task Claude_cli_is_ok_with_its_version_when_it_answers_the_version_flag()
    {
        _processes.Answer("claude", args => Ok(args == "--version" ? "2.1.285 (Claude Code)\n" : "?"));
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.ClaudeCli);

        Assert.Equal(ProviderState.Ok, status.State);
        Assert.Equal("2.1.285 (Claude Code)", status.Detail);
    }

    [Fact]
    public async Task Ffmpeg_is_ok_with_its_version_line_without_the_copyright()
    {
        _processes.Answer("ffmpeg", args => Ok(args == "-version"
            ? "ffmpeg version 6.1.1-full_build-www.gyan.dev Copyright (c) 2000-2023 the FFmpeg developers\nbuilt with gcc"
            : "?"));
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.Ffmpeg);

        Assert.Equal(ProviderState.Ok, status.State);
        Assert.Equal("ffmpeg version 6.1.1-full_build-www.gyan.dev", status.Detail);
    }

    [Fact]
    public async Task A_missing_executable_is_an_error_naming_it()
    {
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.ClaudeCli);

        Assert.Equal(ProviderState.Error, status.State);
        Assert.Equal("not found: claude", status.Detail);
    }

    [Fact]
    public async Task A_version_call_that_fails_is_an_error_with_the_exit_code_and_message()
    {
        _processes.Answer("ffmpeg", _ => new ProcessRunResult(1, "", "Unrecognized option\nmore"));
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.Ffmpeg);

        Assert.Equal(ProviderState.Error, status.State);
        Assert.Equal("exited with code 1: Unrecognized option", status.Detail);
    }

    [Fact]
    public async Task An_empty_executable_means_not_set_up_and_nothing_is_run()
    {
        var client = await ClientAsync();
        var defaults = EngineSettings.Defaults;
        await client.SaveSettingsAsync(defaults with { ClaudeCli = defaults.ClaudeCli with { Executable = " " } });

        var status = await StatusOf(client, ProviderId.ClaudeCli);

        Assert.Equal(ProviderState.NotSetUp, status.State);
        Assert.DoesNotContain(_processes.Calls, call => string.IsNullOrWhiteSpace(call.Executable));
    }

    [Fact]
    public async Task The_saved_executable_is_the_one_that_is_run()
    {
        _processes.Answer(@"C:\tools\claude.cmd", _ => Ok("2.0.0 (Claude Code)"));
        var client = await ClientAsync();
        var defaults = EngineSettings.Defaults;
        await client.SaveSettingsAsync(defaults with { ClaudeCli = defaults.ClaudeCli with { Executable = @"C:\tools\claude.cmd" } });

        Assert.Equal(ProviderState.Ok, (await StatusOf(client, ProviderId.ClaudeCli)).State);
    }

    // --- ComfyUI: GET /system_stats ---

    [Fact]
    public async Task Comfyui_is_ok_with_its_version_when_system_stats_answers()
    {
        _http.Answer("http://127.0.0.1:8188/system_stats", HttpStatusCode.OK,
            """{"system":{"os":"win32","comfyui_version":"0.37.4"},"devices":[]}""");
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.ComfyUi);

        Assert.Equal(ProviderState.Ok, status.State);
        Assert.Equal("ComfyUI 0.37.4", status.Detail);
    }

    [Fact]
    public async Task Comfyui_on_a_port_nobody_listens_on_is_off_and_not_reachable()
    {
        // A real closed port with the real HTTP handler, not a fake.
        var port = FreePort();
        var client = await _engine.StartClientAsync(services => services.AddSingleton<IProcessRunner>(_processes));
        var defaults = EngineSettings.Defaults;
        await client.SaveSettingsAsync(defaults with { ComfyUi = defaults.ComfyUi with { Port = port } });

        var status = await StatusOf(client, ProviderId.ComfyUi);

        Assert.Equal(ProviderState.Off, status.State);
        Assert.Equal($"not reachable at 127.0.0.1:{port}", status.Detail);
    }

    [Fact]
    public async Task Comfyui_answering_with_a_server_error_is_an_error()
    {
        _http.Answer("http://127.0.0.1:8188/system_stats", HttpStatusCode.InternalServerError);
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.ComfyUi);

        Assert.Equal(ProviderState.Error, status.State);
        Assert.Equal("HTTP 500 from /system_stats", status.Detail);
    }

    // --- LM Studio: GET <base url>/models ---

    [Fact]
    public async Task Lm_studio_is_ok_with_its_model_count()
    {
        _http.Answer("http://localhost:1234/v1/models", HttpStatusCode.OK, """{"data":[{"id":"qwen3-32b"},{"id":"gemma-3"}]}""");
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.LmStudio);

        Assert.Equal(ProviderState.Ok, status.State);
        Assert.Equal("2 models", status.Detail);
    }

    [Fact]
    public async Task Lm_studio_not_running_is_off()
    {
        var client = await ClientAsync();

        var status = await StatusOf(client, ProviderId.LmStudio);

        Assert.Equal(ProviderState.Off, status.State);
        Assert.Equal("not reachable at http://localhost:1234/v1", status.Detail);
    }

    [Fact]
    public async Task Lm_studio_without_the_chosen_model_is_an_error()
    {
        _http.Answer("http://localhost:1234/v1/models", HttpStatusCode.OK, """{"data":[{"id":"gemma-3"}]}""");
        var client = await ClientAsync();
        var defaults = EngineSettings.Defaults;
        await client.SaveSettingsAsync(defaults with { LmStudio = defaults.LmStudio with { Model = "qwen3-32b" } });

        var status = await StatusOf(client, ProviderId.LmStudio);

        Assert.Equal(ProviderState.Error, status.State);
        Assert.Equal("model 'qwen3-32b' is not available", status.Detail);
    }

    [Fact]
    public async Task Lm_studio_gets_the_stored_api_token_and_a_rejected_one_is_an_error()
    {
        _http.Answer("http://localhost:1234/v1/models", request =>
            new HttpResponseMessage(request.Headers.Authorization?.Parameter == "tok-123" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        var client = await ClientAsync();

        Assert.Equal(ProviderState.Error, (await StatusOf(client, ProviderId.LmStudio)).State);
        Assert.Equal("API token rejected", (await StatusOf(client, ProviderId.LmStudio)).Detail);

        await client.SetSecretAsync(SecretKey.LmStudioApiToken, "tok-123");
        Assert.Equal(ProviderState.Ok, (await StatusOf(client, ProviderId.LmStudio)).State);
    }

    [Fact]
    public async Task Lm_studio_with_a_base_url_that_is_not_a_url_is_an_error()
    {
        var client = await ClientAsync();
        var defaults = EngineSettings.Defaults;
        await client.SaveSettingsAsync(defaults with { LmStudio = defaults.LmStudio with { BaseUrl = "localhost 1234" } });

        var status = await StatusOf(client, ProviderId.LmStudio);

        Assert.Equal(ProviderState.Error, status.State);
        Assert.Equal("not a valid URL: localhost 1234", status.Detail);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
