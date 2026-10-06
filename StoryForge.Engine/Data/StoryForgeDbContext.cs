using Microsoft.EntityFrameworkCore;

namespace StoryForge.Engine.Data;

/// <summary>The engine's local SQLite database. Tables arrive with the issues that need them.</summary>
public sealed class StoryForgeDbContext(DbContextOptions<StoryForgeDbContext> options) : DbContext(options);
