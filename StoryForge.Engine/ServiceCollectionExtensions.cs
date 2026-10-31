using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;
using StoryForge.Engine.Providers;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Secrets;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine and its in-process <see cref="IStoryForgeClient"/>. The database is
    /// created or migrated when the host starts.
    /// </summary>
    public static IServiceCollection AddStoryForgeEngine(
        this IServiceCollection services, Action<StoryForgeEngineOptions> configure)
    {
        var options = new StoryForgeEngineOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            throw new ArgumentException(
                $"{nameof(StoryForgeEngineOptions.DataDirectory)} must name the folder for the database.",
                nameof(configure));
        }

        // The callback runs once; its result goes into the options pipeline, and both the
        // initializer and the connection string read the folder from there. So the folder the
        // initializer creates is always the folder the database opens in, whatever else
        // configures the options later.
        services.AddOptions<StoryForgeEngineOptions>().Configure(o =>
        {
            o.DataDirectory = options.DataDirectory;
            o.DefaultProjectsFolder = options.DefaultProjectsFolder;
            o.CredentialTargetPrefix = options.CredentialTargetPrefix;
            o.ResearchServerPath = options.ResearchServerPath;
            o.CloseResolveUpdateDialogs = options.CloseResolveUpdateDialogs;
        });
        services.AddDbContextFactory<StoryForgeDbContext>((provider, db) =>
        {
            var path = provider.GetRequiredService<IOptions<StoryForgeEngineOptions>>().Value.DatabasePath;
            db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        });
        services.AddHostedService<DatabaseInitializer>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<HttpMessageHandler>(_ => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) });
        services.AddSingleton<ProviderChecks>();
        services.AddSingleton<IStreamingProcess, StreamingProcess>();
        services.AddSingleton<ClaudeCli>();
        services.AddSingleton<IResearchAgent, ClaudeCliResearchAgent>();
        services.AddSingleton<IStageWorker, ResearchStage>();
        services.AddSingleton<FactSheets>();
        services.AddSingleton<CellReader>();
        services.AddSingleton<Staleness>();
        services.AddSingleton<IScriptAgent, ClaudeCliScriptAgent>();
        services.AddSingleton<IStageWorker, ScriptStage>();
        services.AddSingleton<ScriptSegments>();
        services.AddSingleton<SegmentCells>();
        services.AddSingleton<ProjectFolders>();
        services.AddSingleton<IComfyUi, ComfyUiClient>();
        services.AddSingleton<IAudioConverter, FfmpegAudioConverter>();
        services.AddSingleton<IStageWorker, VoiceStage>();
        services.AddSingleton<VoiceSegments>();
        services.AddSingleton<IStoryboardAgent, ClaudeCliStoryboardAgent>();
        services.AddSingleton<IStageWorker, StoryboardStage>();
        services.AddSingleton<StoryboardShots>();
        services.AddSingleton<StoryboardEdits>();
        // Its own sender, which follows no redirect by itself: every hop is checked (SiteWeb for the wikis, WikiPictures for pictures).
        services.AddSingleton<IPictureSource>(_ => new WikiPictures(WikiPictures.CreateHttp()));
        services.AddSingleton<PoolBuilder>();
        services.AddSingleton<ReferenceSheets>();
        services.AddSingleton<IPictureCheck, ClaudeCliPictureCheck>();
        services.AddSingleton<IStageWorker, ReferencesStage>();
        services.AddSingleton<ReferenceViews>();
        services.AddSingleton<ReferenceEdits>();
        services.AddSingleton<IStillPromptAgent, ClaudeCliStillPromptAgent>();
        services.AddSingleton<IStageWorker, StillsStage>();
        services.AddSingleton<StillCells>();
        services.AddSingleton<TestRenders>();
        services.AddSingleton<Clips.IClipPromptAgent, Clips.ClaudeCliClipPromptAgent>();
        services.AddSingleton<IStageWorker, Clips.ClipsStage>();
        services.AddSingleton<Clips.ClipCells>();
        services.AddSingleton<Assembly.ICutListAgent, Assembly.ClaudeCliCutListAgent>();
        services.AddSingleton<Assembly.IVideoRenderer, Assembly.FfmpegVideoRenderer>();
        services.AddSingleton<Assembly.AssemblyTimeline>();
        services.AddSingleton<IStageWorker, Assembly.AssemblyStage>();
        services.AddSingleton<Resolve.IResolveSenders, Resolve.ResolveSenders>();
        services.AddSingleton<Resolve.ResolveExport>();
        services.AddSingleton<Assembly.AssemblyViews>();
        services.AddSingleton<Reruns>();
        // After DatabaseInitializer: hosted services start in order, and the runner reads the database.
        services.AddSingleton<PipelineRunner>();
        services.AddHostedService(provider => provider.GetRequiredService<PipelineRunner>());
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecretStore, WindowsCredentialStore>();
        }
        else
        {
            services.AddSingleton<ISecretStore, UnsupportedSecretStore>();
        }
        services.AddSingleton<IStoryForgeClient, InProcessStoryForgeClient>();
        return services;
    }
}
