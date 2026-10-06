namespace StoryForge.Engine;

public sealed class StoryForgeEngineOptions
{
    /// <summary>
    /// The folder the engine keeps its database in. The host decides where that is, so the engine
    /// itself never assumes a Windows path.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    internal string DatabasePath => Path.Combine(DataDirectory, "storyforge.db");
}
