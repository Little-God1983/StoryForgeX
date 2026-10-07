using StoryForge.Client;

namespace StoryForge.Engine.Data;

/// <summary>A profile's identity; what it holds lives in its versions.</summary>
public sealed class ProfileEntry
{
    public Guid Id { get; set; }

    public ProfileKind Kind { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ProfileVersionEntry> Versions { get; set; } = [];
}

/// <summary>One saved version of a profile as JSON. Rows are only ever added, never changed.</summary>
public sealed class ProfileVersionEntry
{
    public Guid ProfileId { get; set; }

    public int Version { get; set; }

    public DateTimeOffset SavedAt { get; set; }

    public required string Json { get; set; }
}
