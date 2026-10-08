using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Projects;

/// <summary>
/// Where a project's media goes: a folder per project in the projects folder, e.g.
/// "Documents\StoryForge X\Soul Coins – BG3 lore (3f2a1b4c)". The database keeps paths relative to
/// the projects folder.
/// </summary>
internal sealed class ProjectFolders(SettingsStore settings, IOptions<StoryForgeEngineOptions> options)
{
    /// <summary>The projects folder in use: the saved one, or the engine's default.</summary>
    public async Task<string> RootAsync(CancellationToken cancellationToken)
    {
        var saved = (await settings.LoadAsync(cancellationToken)).Paths.ProjectsFolder;
        return string.IsNullOrWhiteSpace(saved) ? options.Value.EffectiveDefaultProjectsFolder : saved;
    }

    /// <summary>The project's own folder, relative to the projects folder: its name and the start of its id.</summary>
    public static string Of(Project project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. project.Setup.Name.Trim().Select(c => invalid.Contains(c) ? '-' : c)]).TrimEnd('.', ' ');
        if (name.Length > 60)
        {
            name = name[..60].TrimEnd('.', ' ');
        }
        var id = project.Id.ToString("N")[..8];
        return name.Length == 0 ? id : $"{name} ({id})";
    }
}
