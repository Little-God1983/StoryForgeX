using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine.Tests;

public sealed class EngineHostTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // SQLite keeps pooled connections open; release them so the folder can go.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddStoryForgeEngine(options => options.DataDirectory = _dataDirectory);
        return builder.Build();
    }

    [Fact]
    public async Task Starting_the_host_creates_the_database_in_the_data_directory()
    {
        using var host = BuildHost();

        await host.StartAsync();

        Assert.True(File.Exists(Path.Combine(_dataDirectory, "storyforge.db")));
        await host.StopAsync();
    }

    [Fact]
    public async Task Starting_the_host_applies_every_migration()
    {
        using var host = BuildHost();

        await host.StartAsync();

        var factory = host.Services.GetRequiredService<IDbContextFactory<StoryForgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Starting_the_host_twice_keeps_the_existing_database()
    {
        // A marker inside the file, not its timestamp: NTFS tunnelling hands a file deleted and
        // recreated under the same name its old creation time back.
        using (var first = BuildHost())
        {
            await first.StartAsync();
            await ExecuteAsync(first, "PRAGMA user_version = 42");
            await first.StopAsync();
        }

        using var second = BuildHost();
        await second.StartAsync();

        Assert.Equal(42L, await ScalarAsync(second, "PRAGMA user_version"));
        await second.StopAsync();
    }

    private static async Task ExecuteAsync(IHost host, string sql)
    {
        var factory = host.Services.GetRequiredService<IDbContextFactory<StoryForgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<object?> ScalarAsync(IHost host, string sql)
    {
        var factory = host.Services.GetRequiredService<IDbContextFactory<StoryForgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task The_client_reports_every_provider_as_not_set_up()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IStoryForgeClient>();

        var statuses = await client.GetProviderStatusesAsync();

        Assert.Equal(
            ["Claude CLI", "ComfyUI", "CAX", "Resolve"],
            statuses.Select(s => s.Name));
        Assert.All(statuses, s => Assert.Equal(ProviderState.NotSetUp, s.State));
        await host.StopAsync();
    }

    [Fact]
    public async Task The_client_has_no_recent_projects_yet()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<IStoryForgeClient>();

        var projects = await client.GetRecentProjectsAsync();

        Assert.Empty(projects);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_configure_callback_that_names_a_new_folder_on_every_call_still_starts()
    {
        var calls = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddStoryForgeEngine(options =>
            options.DataDirectory = Path.Combine(_dataDirectory, $"call{++calls}"));
        using var host = builder.Build();

        await host.StartAsync();

        var options = host.Services.GetRequiredService<IOptions<StoryForgeEngineOptions>>().Value;
        Assert.True(File.Exists(Path.Combine(options.DataDirectory, "storyforge.db")));
        await host.StopAsync();
    }

    [Fact]
    public void Every_options_interface_sees_the_same_data_directory()
    {
        using var host = BuildHost();

        Assert.Equal(_dataDirectory, host.Services.GetRequiredService<IOptions<StoryForgeEngineOptions>>().Value.DataDirectory);
        Assert.Equal(_dataDirectory, host.Services.GetRequiredService<IOptionsMonitor<StoryForgeEngineOptions>>().CurrentValue.DataDirectory);
        using var scope = host.Services.CreateScope();
        Assert.Equal(_dataDirectory, scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<StoryForgeEngineOptions>>().Value.DataDirectory);
    }

    [Fact]
    public async Task A_data_directory_with_connection_string_characters_works()
    {
        var folder = Path.Combine(_dataDirectory, "a;b=c");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddStoryForgeEngine(options => options.DataDirectory = folder);
        using var host = builder.Build();

        await host.StartAsync();

        Assert.True(File.Exists(Path.Combine(folder, "storyforge.db")));
        await host.StopAsync();
    }

    [Fact]
    public void A_missing_data_directory_is_rejected_when_the_engine_is_registered()
    {
        var builder = Host.CreateApplicationBuilder();

        var error = Assert.Throws<ArgumentException>(
            () => builder.Services.AddStoryForgeEngine(_ => { }));

        Assert.Contains(nameof(StoryForgeEngineOptions.DataDirectory), error.Message);
    }
}
