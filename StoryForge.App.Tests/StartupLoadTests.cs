using StoryForge.App.ViewModels;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class StartupLoadTests
{
    private readonly FakeStoryForgeClient _client = new();

    private MainViewModel Main() => new(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);

    private void AllProviders(ProviderState state, string? detail = null)
    {
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            _client.Providers.Add(new(id, id.ToString(), state, detail));
        }
    }

    [Fact]
    public async Task The_top_bar_shows_claude_comfyui_cax_and_resolve_in_that_order()
    {
        AllProviders(ProviderState.NotSetUp);
        var main = Main();

        await main.LoadAsync();

        Assert.Equal(
            [ProviderId.ClaudeCli, ProviderId.ComfyUi, ProviderId.ContentAutomatorX, ProviderId.DavinciResolve],
            main.ProviderPills.Select(pill => pill.Id));
    }

    [Theory]
    [InlineData(ProviderState.NotSetUp, "ComfyUI · not set up")]
    [InlineData(ProviderState.Ok, "ComfyUI · ok")]
    [InlineData(ProviderState.Off, "ComfyUI · off")]
    [InlineData(ProviderState.Error, "ComfyUI · error")]
    public async Task A_pill_names_its_provider_and_state(ProviderState state, string text)
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", state, "not reachable at 127.0.0.1:8188"));
        var main = Main();

        await main.LoadAsync();

        var pill = main.ProviderPills.Single(p => p.Id == ProviderId.ComfyUi);
        Assert.Equal(text, pill.Text);
        Assert.Equal("not reachable at 127.0.0.1:8188", pill.Detail);
    }

    [Fact]
    public void The_pills_are_there_from_the_start_and_say_checking()
    {
        var main = Main();

        Assert.Equal(4, main.ProviderPills.Count);
        Assert.Equal("ComfyUI · checking…", main.ProviderPills.Single(p => p.Id == ProviderId.ComfyUi).Text);
    }

    [Fact]
    public async Task A_refresh_updates_the_existing_pills_instead_of_replacing_them()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Ok));
        var main = Main();
        var before = main.ProviderPills.ToList();

        await main.LoadAsync();

        Assert.Equal(before, main.ProviderPills);
    }

    [Fact]
    public async Task A_failing_first_check_does_not_fail_loading()
    {
        _client.StatusFailure = new InvalidOperationException("settings unreadable");
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins", "Script · running"));
        var main = Main();

        await main.LoadAsync();

        Assert.Single(main.RecentProjects);
    }

    [Fact]
    public async Task Refreshing_the_board_updates_the_pills()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Off));
        var board = new ProviderStatusBoard(_client);
        var main = new MainViewModel(_client, board, TimeSpan.Zero);
        await main.LoadAsync();

        _client.Providers[0] = new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Ok, "ComfyUI 0.37.4");
        await board.RefreshAsync();

        Assert.Equal("ComfyUI · ok", main.ProviderPills.Single(p => p.Id == ProviderId.ComfyUi).Text);
    }

    [Fact]
    public async Task With_no_projects_the_recent_list_is_empty()
    {
        var main = Main();

        await main.LoadAsync();

        Assert.Empty(main.RecentProjects);
        Assert.False(main.HasRecentProjects);
    }

    [Fact]
    public async Task Recent_projects_are_listed_with_their_status_line()
    {
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins – BG3 lore", "Stills · awaiting review"));
        var main = Main();

        await main.LoadAsync();

        var project = Assert.Single(main.RecentProjects);
        Assert.Equal("Soul Coins – BG3 lore", project.Name);
        Assert.Equal("Stills · awaiting review", project.StatusLine);
        Assert.True(main.HasRecentProjects);
    }

    [Fact]
    public async Task Recent_projects_that_cannot_be_read_show_the_error_in_their_place()
    {
        // Loading runs after the window is open; a locked database must not end the app.
        _client.RecentProjectsFailure = new InvalidOperationException("database is locked");
        AllProviders(ProviderState.Ok);
        var main = Main();

        await main.LoadAsync();

        Assert.Contains("database is locked", main.RecentProjectsError);
        Assert.Equal(1, _client.StatusChecks);   // the rest of the loading still ran
    }

    [Fact]
    public async Task Settings_that_cannot_be_read_do_not_stop_the_rest_of_loading()
    {
        _client.SettingsLoadFailure = new InvalidOperationException("database is locked");
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins", "Not started"));
        var main = Main();

        var failure = await Assert.ThrowsAnyAsync<Exception>(main.LoadAsync);

        Assert.Contains("database is locked", failure.Message);
        Assert.Single(main.RecentProjects);
        Assert.Equal(1, _client.StatusChecks);
    }

    [Fact]
    public async Task Loading_twice_does_not_duplicate_projects_and_keeps_the_four_pills()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.NotSetUp));
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins", "Script · running"));
        var main = Main();

        await main.LoadAsync();
        await main.LoadAsync();

        Assert.Equal(4, main.ProviderPills.Count);
        Assert.Single(main.RecentProjects);
    }
}
