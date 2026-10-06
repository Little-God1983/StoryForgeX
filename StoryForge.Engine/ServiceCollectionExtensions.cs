using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // The callback runs once; its result goes into the options pipeline, and both the
        // initializer and the connection string read the folder from there. So the folder the
        // initializer creates is always the folder the database opens in, whatever else
        // configures the options later.
        services.AddOptions<StoryForgeEngineOptions>().Configure(o => o.DataDirectory = options.DataDirectory);
        services.AddDbContextFactory<StoryForgeDbContext>((provider, db) =>
        {
            var path = provider.GetRequiredService<IOptions<StoryForgeEngineOptions>>().Value.DatabasePath;
            db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        });
        services.AddHostedService<DatabaseInitializer>();
        services.AddSingleton<IStoryForgeClient, InProcessStoryForgeClient>();
        return services;
    }
}
