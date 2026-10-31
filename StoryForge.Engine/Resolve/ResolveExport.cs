using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Settings;

namespace StoryForge.Engine.Resolve;

/// <summary>Makes the sender for an install: the real one runs ResolvePython; tests pass their own.</summary>
internal interface IResolveSenders
{
    IResolveSender For(ResolveInstall install);
}

internal sealed class ResolveSenders(IProcessRunner processes, IOptions<StoryForgeEngineOptions> options) : IResolveSenders
{
    /// <summary>Beside the app, as a file: an interpreter cannot be handed an embedded resource.</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Resolve", "send_to_resolve.py");

    public IResolveSender For(ResolveInstall install) =>
        new ResolveSender(install, ScriptPath, ResolveSender.Interpreter(processes, install), options.Value.CloseResolveUpdateDialogs);
}

/// <summary>Send to Resolve (#71): an Assembly version's timeline as a new, named Resolve project.</summary>
internal sealed class ResolveExport(CellReader cells, ProjectStore projects, ProjectFolders folders, SettingsStore settings, IResolveSenders senders)
{
    public async Task<SentToResolve> SendAsync(Guid projectId, int version, string projectName, CancellationToken cancellationToken)
    {
        var name = projectName?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException("Give the Resolve project a name.", nameof(projectName));
        }
        await projects.GetAsync(projectId, cancellationToken);
        var video = await cells.AssemblyVersionAsync(projectId, version, cancellationToken)
            ?? throw new KeyNotFoundException($"The Assembly has no version {version}.");
        var root = await folders.RootAsync(cancellationToken);
        if (video.Timeline is null || !File.Exists(Path.Combine(root, video.Timeline)))
        {
            return new SentToResolve(false, "timeline",
                $"Version {version} was rendered without a timeline file. With \"Also export timeline\" on in Settings → FFmpeg, the next render writes one.", null, 0);
        }
        var saved = (await settings.LoadAsync(cancellationToken)).Resolve;
        var install = ResolveInstall.Locate(saved.ResolveExe, saved.ResolvePython);
        var request = new SendToResolveRequest(name, Path.Combine(root, video.Timeline),
            SendToResolveRequest.FrameRateText(ClipsStage.FramesPerSecond, 1), PlaceMarkers: false, Markers: []);
        var result = await senders.For(install).SendAsync(request, cancellationToken);
        return new SentToResolve(result.Ok, result.Stage, result.Problem, result.TimelineName, result.VideoItems);
    }
}
