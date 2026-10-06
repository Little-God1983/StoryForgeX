using System.Text;
using StoryForge.Client;

namespace StoryForge.Engine.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly EngineTestHost _engine = new();

    public void Dispose() => _engine.Dispose();

    private static EngineSettings Changed() => EngineSettings.Defaults with
    {
        ClaudeCli = new ClaudeCliSettings(@"C:\tools\claude.cmd", "-p", TimeoutSeconds: 120, MaxParallel: 3),
        LmStudio = new LmStudioSettings("http://gpu-box:1234/v1", "qwen3-32b", StructuredOutputMode.PromptOnlyValidate, WebResearchMode.None),
        ComfyUi = new ComfyUiSettings("10.0.0.5", 8190, @"D:\workflows", GpuSlots: 2, FreeVramBetweenStages: false),
        Ffmpeg = new FfmpegSettings(@"C:\ffmpeg\bin\ffmpeg.exe", TimelineExport.OpenTimelineIo),
        Paths = new PathSettings(@"D:\StoryForge"),
    };

    [Fact]
    public async Task A_fresh_engine_returns_the_defaults()
    {
        var client = await _engine.StartClientAsync();

        Assert.Equal(EngineSettings.Defaults, await client.GetSettingsAsync());
    }

    [Fact]
    public async Task Saved_settings_survive_a_restart()
    {
        var first = await _engine.StartClientAsync();
        await first.SaveSettingsAsync(Changed());

        var second = await _engine.StartClientAsync();

        Assert.Equal(Changed(), await second.GetSettingsAsync());
    }

    [Theory]
    [InlineData("port")]
    [InlineData("timeout")]
    [InlineData("parallel")]
    [InlineData("gpu")]
    public async Task Out_of_range_numbers_are_rejected_and_nothing_is_saved(string field)
    {
        var client = await _engine.StartClientAsync();
        var d = EngineSettings.Defaults;
        var bad = field switch
        {
            "port" => d with { ComfyUi = d.ComfyUi with { Port = 70000 } },
            "timeout" => d with { ClaudeCli = d.ClaudeCli with { TimeoutSeconds = 0 } },
            "parallel" => d with { ClaudeCli = d.ClaudeCli with { MaxParallel = 0 } },
            _ => d with { ComfyUi = d.ComfyUi with { GpuSlots = 0 } },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SaveSettingsAsync(bad));

        Assert.Equal(EngineSettings.Defaults, await client.GetSettingsAsync());
    }

    [Fact]
    public async Task Saves_running_at_the_same_time_on_a_fresh_database_all_succeed()
    {
        var client = await _engine.StartClientAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            client.SaveSettingsAsync(Changed() with { Paths = new PathSettings($@"D:\run{i}") })));

        Assert.StartsWith(@"D:\run", (await client.GetSettingsAsync()).Paths.ProjectsFolder);
    }

    [Fact]
    public async Task Paths_pasted_with_quotes_are_saved_without_them()
    {
        // Explorer's "Copy as path" wraps the path in quotes.
        var client = await _engine.StartClientAsync();
        var d = EngineSettings.Defaults;

        await client.SaveSettingsAsync(d with
        {
            ClaudeCli = d.ClaudeCli with { Executable = "\"C:\\Program Files\\nodejs\\claude.cmd\"" },
            Ffmpeg = d.Ffmpeg with { Executable = " \"C:\\ffmpeg\\bin\\ffmpeg.exe\" " },
            ComfyUi = d.ComfyUi with { WorkflowTemplatesFolder = "\"D:\\workflows\"" },
            Paths = new PathSettings("\"D:\\StoryForge\""),
        });

        var saved = await client.GetSettingsAsync();
        Assert.Equal(@"C:\Program Files\nodejs\claude.cmd", saved.ClaudeCli.Executable);
        Assert.Equal(@"C:\ffmpeg\bin\ffmpeg.exe", saved.Ffmpeg.Executable);
        Assert.Equal(@"D:\workflows", saved.ComfyUi.WorkflowTemplatesFolder);
        Assert.Equal(@"D:\StoryForge", saved.Paths.ProjectsFolder);
    }

    [Fact]
    public async Task An_unreadable_settings_row_falls_back_to_its_defaults_and_the_rest_still_loads()
    {
        var client = await _engine.StartClientAsync();
        await client.SaveSettingsAsync(Changed());
        await WriteRowAsync("providers.ffmpeg", """{"Executable":"ffmpeg","TimelineExport":"RenamedInAFutureVersion"}""");

        var loaded = await client.GetSettingsAsync();

        Assert.Equal(EngineSettings.Defaults.Ffmpeg, loaded.Ffmpeg);
        Assert.Equal(Changed().ComfyUi, loaded.ComfyUi);
    }

    [Fact]
    public async Task A_field_missing_from_an_older_saved_row_gets_its_default()
    {
        var client = await _engine.StartClientAsync();
        await client.SaveSettingsAsync(Changed());
        await WriteRowAsync("providers.claude-cli", """{"Executable":"claude.cmd","TimeoutSeconds":120,"MaxParallel":3}""");

        var claude = (await client.GetSettingsAsync()).ClaudeCli;

        Assert.Equal("claude.cmd", claude.Executable);
        Assert.Equal(EngineSettings.Defaults.ClaudeCli.Arguments, claude.Arguments);
        Assert.Equal(120, claude.TimeoutSeconds);
    }

    [Fact]
    public async Task A_null_in_a_saved_row_gets_the_default_too()
    {
        var client = await _engine.StartClientAsync();
        await client.SaveSettingsAsync(Changed());
        await WriteRowAsync("providers.lm-studio", """{"BaseUrl":null,"Model":"qwen3-32b"}""");

        var lmStudio = (await client.GetSettingsAsync()).LmStudio;

        Assert.Equal(EngineSettings.Defaults.LmStudio.BaseUrl, lmStudio.BaseUrl);
        Assert.Equal("qwen3-32b", lmStudio.Model);
    }

    private async Task WriteRowAsync(string key, string json)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_engine.DataDirectory, "storyforge.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Settings SET Json = $json WHERE Key = $key";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$key", key);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task A_secret_can_be_stored_and_removed()
    {
        var client = await _engine.StartClientAsync();
        Assert.False(await client.HasSecretAsync(SecretKey.LmStudioApiToken));

        await client.SetSecretAsync(SecretKey.LmStudioApiToken, "tok-123");
        Assert.True(await client.HasSecretAsync(SecretKey.LmStudioApiToken));

        await client.SetSecretAsync(SecretKey.LmStudioApiToken, null);
        Assert.False(await client.HasSecretAsync(SecretKey.LmStudioApiToken));
    }

    [Fact]
    public async Task A_stored_secret_never_reaches_the_database_file()
    {
        var client = await _engine.StartClientAsync();
        var token = "secret-" + Guid.NewGuid().ToString("N");

        await client.SetSecretAsync(SecretKey.LmStudioApiToken, token);
        await client.SaveSettingsAsync(Changed());

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_engine.DataDirectory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        }
    }

    [Fact]
    public async Task The_projects_folder_defaults_to_a_folder_inside_the_data_directory()
    {
        var client = await _engine.StartClientAsync();

        Assert.Equal(Path.Combine(_engine.DataDirectory, "projects"), await client.GetEffectiveProjectsFolderAsync());
    }

    [Fact]
    public async Task A_saved_projects_folder_is_used()
    {
        var client = await _engine.StartClientAsync();

        await client.SaveSettingsAsync(Changed());

        Assert.Equal(@"D:\StoryForge", await client.GetEffectiveProjectsFolderAsync());
    }
}
