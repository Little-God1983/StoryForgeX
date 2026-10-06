using StoryForge.App.ViewModels;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class StartupLoadTests
{
    private readonly FakeStoryForgeClient _client = new();

    [Fact]
    public async Task Loading_shows_one_status_pill_per_provider_in_order()
    {
        _client.Providers.Add(new("Claude CLI", ProviderState.NotSetUp));
        _client.Providers.Add(new("ComfyUI", ProviderState.NotSetUp));
        var main = new MainViewModel(_client);

        await main.LoadAsync();

        Assert.Equal(["Claude CLI", "ComfyUI"], main.ProviderPills.Select(pill => pill.Name));
    }

    [Fact]
    public async Task A_provider_that_is_not_set_up_says_so_on_its_pill()
    {
        _client.Providers.Add(new("ComfyUI", ProviderState.NotSetUp));
        var main = new MainViewModel(_client);

        await main.LoadAsync();

        Assert.Equal("ComfyUI · not set up", main.ProviderPills.Single().Text);
    }

    [Fact]
    public async Task With_no_projects_the_recent_list_is_empty()
    {
        var main = new MainViewModel(_client);

        await main.LoadAsync();

        Assert.Empty(main.RecentProjects);
        Assert.False(main.HasRecentProjects);
    }

    [Fact]
    public async Task Recent_projects_are_listed_with_their_status_line()
    {
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins – BG3 lore", "Stills · awaiting review"));
        var main = new MainViewModel(_client);

        await main.LoadAsync();

        var project = Assert.Single(main.RecentProjects);
        Assert.Equal("Soul Coins – BG3 lore", project.Name);
        Assert.Equal("Stills · awaiting review", project.StatusLine);
        Assert.True(main.HasRecentProjects);
    }

    [Fact]
    public async Task Loading_twice_does_not_duplicate_pills_or_projects()
    {
        _client.Providers.Add(new("ComfyUI", ProviderState.NotSetUp));
        _client.RecentProjects.Add(new(Guid.NewGuid(), "Soul Coins", "Script · running"));
        var main = new MainViewModel(_client);

        await main.LoadAsync();
        await main.LoadAsync();

        Assert.Single(main.ProviderPills);
        Assert.Single(main.RecentProjects);
    }
}
