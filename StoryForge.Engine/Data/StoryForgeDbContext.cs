using Microsoft.EntityFrameworkCore;

namespace StoryForge.Engine.Data;

/// <summary>The engine's local SQLite database. Tables arrive with the issues that need them.</summary>
public sealed class StoryForgeDbContext(DbContextOptions<StoryForgeDbContext> options) : DbContext(options)
{
    public DbSet<SettingsEntry> Settings => Set<SettingsEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingsEntry>(entry =>
        {
            entry.ToTable("Settings");
            entry.HasKey(e => e.Key);
        });
    }
}
