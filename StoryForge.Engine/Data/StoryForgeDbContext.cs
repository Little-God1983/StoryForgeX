using Microsoft.EntityFrameworkCore;

namespace StoryForge.Engine.Data;

/// <summary>The engine's local SQLite database. Tables arrive with the issues that need them.</summary>
public sealed class StoryForgeDbContext(DbContextOptions<StoryForgeDbContext> options) : DbContext(options)
{
    public DbSet<SettingsEntry> Settings => Set<SettingsEntry>();

    public DbSet<ProfileEntry> Profiles => Set<ProfileEntry>();

    public DbSet<ProfileVersionEntry> ProfileVersions => Set<ProfileVersionEntry>();

    public DbSet<ProjectEntry> Projects => Set<ProjectEntry>();

    public DbSet<CellEntry> Cells => Set<CellEntry>();

    public DbSet<CellVersionEntry> CellVersions => Set<CellVersionEntry>();

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

        modelBuilder.Entity<ProjectEntry>(project =>
        {
            project.ToTable("Projects");
            project.HasKey(p => p.Id);
        });

        // Enums by name throughout, so adding a stage or state never changes what is stored.
        modelBuilder.Entity<CellEntry>(cell =>
        {
            cell.ToTable("Cells");
            cell.HasKey(c => new { c.ProjectId, c.Stage, c.Key });
            cell.Property(c => c.Stage).HasConversion<string>();
            cell.Property(c => c.State).HasConversion<string>();
            cell.HasOne<ProjectEntry>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CellVersionEntry>(version =>
        {
            version.ToTable("CellVersions");
            version.HasKey(v => new { v.ProjectId, v.Stage, v.Key, v.Version });
            version.Property(v => v.Stage).HasConversion<string>();
            version.Property(v => v.Origin).HasConversion<string>();
            version.HasOne<CellEntry>().WithMany().HasForeignKey(v => new { v.ProjectId, v.Stage, v.Key }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
