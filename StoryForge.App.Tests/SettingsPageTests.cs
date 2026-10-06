using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class SettingsPageTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly ProviderStatusBoard _board;

    public SettingsPageTests() => _board = new ProviderStatusBoard(_client);

    private async Task<SettingsPageViewModel> LoadedPage(TimeSpan? saveDelay = null)
    {
        var page = new SettingsPageViewModel(_client, _board, saveDelay ?? TimeSpan.Zero);
        await page.LoadAsync();
        return page;
    }

    [Fact]
    public async Task Loading_fills_every_card_from_the_saved_settings()
    {
        _client.Settings = EngineSettings.Defaults with
        {
            ClaudeCli = new ClaudeCliSettings("claude.cmd", "-p", 120, 3),
            ComfyUi = new ComfyUiSettings("10.0.0.5", 8190, @"D:\wf", 2, false),
        };

        var page = await LoadedPage();

        Assert.Equal("claude.cmd", page.ClaudeCli.Executable);
        Assert.Equal(120, page.ClaudeCli.TimeoutSeconds);
        Assert.Equal("10.0.0.5", page.ComfyUi.Host);
        Assert.Equal(8190, page.ComfyUi.Port);
        Assert.False(page.ComfyUi.FreeVramBetweenStages);
        Assert.Equal("ffmpeg", page.Ffmpeg.Executable);
        Assert.Equal(StructuredOutputMode.JsonSchema, page.LmStudio.StructuredOutput);
    }

    [Fact]
    public async Task Settings_are_filled_before_the_slow_provider_checks_finish()
    {
        var gate = new TaskCompletionSource();
        _client.StatusGate = gate.Task;
        var main = new MainViewModel(_client, _board, TimeSpan.Zero);
        var settings = (SettingsPageViewModel)main.NavItems.Single(item => item.Title == "Settings").Page;

        var loading = main.LoadAsync();

        Assert.Equal("claude", settings.ClaudeCli.Executable);
        Assert.Equal("checking…", settings.ClaudeCli.StateText);
        gate.SetResult();
        await loading;
    }

    [Fact]
    public async Task A_provider_the_checks_did_not_report_shows_not_set_up_after_a_check()
    {
        var page = await LoadedPage();

        await _board.RefreshAsync();

        Assert.Equal("not set up", page.ComfyUi.StateText);
    }

    [Fact]
    public async Task Loading_does_not_save()
    {
        await LoadedPage();

        Assert.Equal(0, _client.Saves);
    }

    [Fact]
    public async Task Changing_a_field_saves_it_and_rechecks_the_providers()
    {
        var page = await LoadedPage();
        var checksBefore = _client.StatusChecks;

        page.ComfyUi.Port = 8190;
        await page.PendingSave;

        Assert.Equal(8190, _client.Settings.ComfyUi.Port);
        Assert.True(_client.StatusChecks > checksBefore);
    }

    [Fact]
    public async Task Quick_changes_in_a_row_are_saved_once()
    {
        var page = await LoadedPage(saveDelay: TimeSpan.FromMilliseconds(200));

        page.ClaudeCli.Executable = "c";
        page.ClaudeCli.Executable = "cl";
        page.ClaudeCli.Executable = "claude.cmd";
        await page.PendingSave;

        Assert.Equal(1, _client.Saves);
        Assert.Equal("claude.cmd", _client.Settings.ClaudeCli.Executable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task An_out_of_range_port_shows_an_error_and_is_not_saved(int port)
    {
        var page = await LoadedPage();

        page.ComfyUi.Port = port;
        await page.PendingSave;

        Assert.True(page.ComfyUi.HasErrors);
        Assert.Equal(0, _client.Saves);
    }

    [Fact]
    public async Task Fixing_an_invalid_field_saves_again()
    {
        var page = await LoadedPage();
        page.ClaudeCli.MaxParallel = 0;
        await page.PendingSave;

        page.ClaudeCli.MaxParallel = 4;
        await page.PendingSave;

        Assert.False(page.ClaudeCli.HasErrors);
        Assert.Equal(4, _client.Settings.ClaudeCli.MaxParallel);
    }

    [Fact]
    public async Task A_rejected_save_shows_the_reason()
    {
        var page = await LoadedPage();
        _client.SaveFailure = new ArgumentException("ComfyUI port must be between 1 and 65535.");

        page.ComfyUi.Host = "gpu-box";
        await page.PendingSave;

        Assert.Equal("ComfyUI port must be between 1 and 65535.", page.SaveError);
    }

    [Fact]
    public async Task Each_card_shows_its_providers_status_and_reason()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Off, "not reachable at 127.0.0.1:8190"));
        _client.Providers.Add(new(ProviderId.ClaudeCli, "Claude CLI", ProviderState.Ok, "2.1.285 (Claude Code)"));
        var page = await LoadedPage();

        await _board.RefreshAsync();

        Assert.Equal(ProviderState.Off, page.ComfyUi.State);
        Assert.Equal("off", page.ComfyUi.StateText);
        Assert.Equal("not reachable at 127.0.0.1:8190", page.ComfyUi.Detail);
        Assert.Equal("ok", page.ClaudeCli.StateText);
        Assert.Equal("2.1.285 (Claude Code)", page.ClaudeCli.Detail);
    }

    [Fact]
    public async Task The_lm_studio_token_goes_to_the_secret_store_and_can_be_removed()
    {
        var page = await LoadedPage();
        Assert.False(page.LmStudio.HasApiToken);

        await page.LmStudio.SaveApiTokenAsync("tok-123");

        Assert.Equal("tok-123", _client.Secrets[SecretKey.LmStudioApiToken]);
        Assert.True(page.LmStudio.HasApiToken);
        Assert.Equal(0, _client.Saves);

        await page.LmStudio.RemoveApiTokenAsync();

        Assert.False(page.LmStudio.HasApiToken);
        Assert.Empty(_client.Secrets);
    }

    [Fact]
    public async Task Changing_the_token_rechecks_the_providers()
    {
        var page = await LoadedPage();
        var checksBefore = _client.StatusChecks;

        await page.LmStudio.SaveApiTokenAsync("tok-123");

        Assert.True(_client.StatusChecks > checksBefore);
    }

    [Fact]
    public async Task The_side_menu_lists_the_settings_sections_and_opens_on_providers()
    {
        var page = await LoadedPage();

        Assert.Equal(
            ["Providers", "Storage", "Assembly & export", "Paths & cache", "Engine host"],
            page.Sections.Select(s => s.Title));
        Assert.Same(page.Sections[0], page.SelectedSection);
        Assert.Equal("Settings / Providers", page.Breadcrumb);
    }

    [Fact]
    public async Task Choosing_a_section_updates_the_breadcrumb_in_the_top_bar()
    {
        var main = new MainViewModel(_client, _board, TimeSpan.Zero);
        await main.LoadAsync();
        var settings = (SettingsPageViewModel)main.NavItems.Single(item => item.Title == "Settings").Page;
        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Settings");

        settings.SelectedSection = settings.Sections.Single(s => s.Title == "Paths & cache");

        Assert.Equal("Settings / Paths & cache", main.Breadcrumb);
    }

    [Fact]
    public async Task The_projects_folder_shows_the_folder_in_use_and_saves_a_new_one()
    {
        var page = await LoadedPage();
        Assert.Equal(@"C:\Data\projects", page.EffectiveProjectsFolder);

        page.ProjectsFolder = @"D:\StoryForge";
        await page.PendingSave;

        Assert.Equal(@"D:\StoryForge", _client.Settings.Paths.ProjectsFolder);
        Assert.Equal(@"D:\StoryForge", page.EffectiveProjectsFolder);
    }
}
