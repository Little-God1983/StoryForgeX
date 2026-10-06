using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StoryForge.Engine.Data;

/// <summary>Used only by `dotnet ef` to create migrations; the app builds its context through DI.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StoryForgeDbContext>
{
    public StoryForgeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<StoryForgeDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
