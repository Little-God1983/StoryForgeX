namespace StoryForge.Client;

/// <summary>One entry in the "Recent projects" list.</summary>
/// <param name="StatusLine">Where the project stands, e.g. "Stills · awaiting review".</param>
public sealed record ProjectSummary(Guid Id, string Name, string StatusLine);
