namespace StoryForge.Engine.Data;

/// <summary>One saved settings group (e.g. "providers.comfyui") as JSON. Never holds a secret.</summary>
public sealed class SettingsEntry
{
    public required string Key { get; set; }

    public required string Json { get; set; }
}
