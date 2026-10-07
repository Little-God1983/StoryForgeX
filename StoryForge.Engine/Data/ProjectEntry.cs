namespace StoryForge.Engine.Data;

/// <summary>A project: its name for listing, and its whole setup as JSON.</summary>
public sealed class ProjectEntry
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When anything about the project last changed; Recent projects sort by it.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public required string SetupJson { get; set; }
}
