using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryForge.Client;
using StoryForge.Engine.Data;

namespace StoryForge.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine and its in-process <see cref="IStoryForgeClient"/>. The database is
    /// created or migrated when the host starts.
    /// </summary>
    public static IServiceCollection AddStoryForgeEngine(
        this IServiceCollection services, Action<StoryForgeEngineOptions> configure)
    {
        var options = new StoryForgeEngineOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            throw new ArgumentException(
                $"{nameof(StoryForgeEngineOptions.DataDirectory)} must name the folder for the database.",
                nameof(configure));
        }

        // The callback runs once; its result feeds both the connection string and the options
        // pipeline (IOptions, IOptionsMonitor, IOptionsSnapshot), so the folder the initializer
        // creates is always the folder the database opens in.
        services.AddOptions<StoryForgeEngineOptions>().Configure(o => o.DataDirectory = options.DataDirectory);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath }.ToString();
        services.AddDbContextFactory<StoryForgeDbContext>(db => db.UseSqlite(connectionString));
        services.AddHostedService<DatabaseInitializer>();
        services.AddSingleton<IStoryForgeClient, InProcessStoryForgeClient>();
        return services;
    }
}
