namespace StoryForge.Engine;

public sealed class StoryForgeEngineOptions
{
    /// <summary>
    /// The folder the engine keeps its database in. The host decides where that is, so the engine
    /// itself never assumes a Windows path.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Where project media goes when the user has not chosen a folder; empty means DataDirectory/projects.</summary>
    public string DefaultProjectsFolder { get; set; } = "";

    /// <summary>Prefix for the engine's entries in the OS credential store (tests use their own).</summary>
    public string CredentialTargetPrefix { get; set; } = "StoryForgeX";

    /// <summary>The research MCP server's exe; empty means the one next to the app.</summary>
    public string ResearchServerPath { get; set; } = "";

    internal string DatabasePath => Path.Combine(DataDirectory, "storyforge.db");

    internal string EffectiveResearchServerPath =>
        string.IsNullOrWhiteSpace(ResearchServerPath)
            ? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "StoryForge.ResearchServer.exe" : "StoryForge.ResearchServer")
            : ResearchServerPath;

    internal string EffectiveDefaultProjectsFolder =>
        string.IsNullOrWhiteSpace(DefaultProjectsFolder) ? Path.Combine(DataDirectory, "projects") : DefaultProjectsFolder;
}
