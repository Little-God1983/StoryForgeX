using Microsoft.EntityFrameworkCore;

namespace StoryForge.Engine.Data;

/// <summary>The engine's local SQLite database. Tables arrive with the issues that need them.</summary>
public sealed class StoryForgeDbContext(DbContextOptions<StoryForgeDbContext> options) : DbContext(options)
{
    public DbSet<SettingsEntry> Settings => Set<SettingsEntry>();

    public DbSet<ProfileEntry> Profiles => Set<ProfileEntry>();

    public DbSet<ProfileVersionEntry> ProfileVersions => Set<ProfileVersionEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingsEntry>(entry =>
        {
            entry.ToTable("Settings");
            entry.HasKey(e => e.Key);
        });

        modelBuilder.Entity<ProfileEntry>(profile =>
        {
            profile.ToTable("Profiles");
            profile.HasKey(p => p.Id);
            // By name, so reordering the enum never turns an image profile into a video one.
            profile.Property(p => p.Kind).HasConversion<string>();
            profile.Property(p => p.Name).UseCollation("NOCASE");
            profile.HasIndex(p => new { p.Kind, p.Name }).IsUnique();
            profile.HasMany(p => p.Versions).WithOne().HasForeignKey(v => v.ProfileId);
        });

        modelBuilder.Entity<ProfileVersionEntry>(version =>
        {
            version.ToTable("ProfileVersions");
            version.HasKey(v => new { v.ProfileId, v.Version });
        });
    }
}
