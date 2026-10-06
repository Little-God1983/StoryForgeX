using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace StoryForge.Engine.Data;

/// <summary>
/// Brings the database up to date when the host starts: creates it on first start and applies any
/// migration that is not in it yet. Runs before the app shows its window.
/// </summary>
internal sealed class DatabaseInitializer(
    IDbContextFactory<StoryForgeDbContext> contextFactory,
    IOptions<StoryForgeEngineOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
