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
    public async Task Refreshing_the_board_updates_the_pills()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Off));
        var board = new ProviderStatusBoard(_client);
        var main = new MainViewModel(_client, board, TimeSpan.Zero);
        await main.LoadAsync();

        _client.Providers[0] = new(ProviderId.ComfyUi, "ComfyUI", ProviderState.Ok, "ComfyUI 0.37.4");
        await board.RefreshAsync();

        Assert.Equal("ComfyUI · ok", main.ProviderPills.Single().Text);
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
    public async Task Loading_twice_does_not_duplicate_pills_or_projects()
    {
        _client.Providers.Add(new(ProviderId.ComfyUi, "ComfyUI", ProviderState.NotSetUp));
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins", "Script · running"));
        var main = Main();

        await main.LoadAsync();
        await main.LoadAsync();

        Assert.Single(main.ProviderPills);
        Assert.Single(main.RecentProjects);
    }
}
