using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;
using StoryForge.Engine.References;
using StoryForge.Engine.Script;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>
/// A temporary data folder plus engine hosts built on it. Each test class gets its own folder and
/// its own credential-store prefix, so tests never see each other's (or the real app's) data.
/// </summary>
internal sealed class EngineTestHost : IDisposable
{
    private readonly List<IHost> _hosts = [];

    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public string CredentialPrefix { get; } = "StoryForgeX.Tests." + Guid.NewGuid().ToString("N");

    /// <summary>Builds and starts a host; <paramref name="replace"/> swaps engine services for fakes.</summary>
    public async Task<IHost> StartAsync(Action<IServiceCollection>? replace = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddStoryForgeEngine(options =>
        {
            options.DataDirectory = DataDirectory;
            options.CredentialTargetPrefix = CredentialPrefix;
        });
        // No test ever reaches the real Claude CLI, ComfyUI, ffmpeg or the sources online: they refuse (or find
        // nothing) unless a test puts its own in.
        builder.Services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IStoryboardAgent>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IPictureCheck>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IPictureSource>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IComfyUi>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IAudioConverter>(new NoModel()));
        replace?.Invoke(builder.Services);
        var host = builder.Build();
        await host.StartAsync();
        _hosts.Add(host);
        return host;
    }

    /// <summary>The database of the last host started, for tests that set up what the client cannot.</summary>
    public Task<StoryForgeDbContext> DbAsync() =>
        _hosts[^1].Services.GetRequiredService<IDbContextFactory<StoryForgeDbContext>>().CreateDbContextAsync();

    public async Task<IStoryForgeClient> StartClientAsync(Action<IServiceCollection>? replace = null) =>
        (await StartAsync(replace)).Services.GetRequiredService<IStoryForgeClient>();

    /// <summary>A model (or ComfyUI, or ffmpeg) that is not there: every stage that asks it fails with a plain reason.</summary>
    private sealed class NoModel : IResearchAgent, IScriptAgent, IStoryboardAgent, IPictureCheck, IPictureSource, IComfyUi, IAudioConverter
    {
        private const string Reason = "No model in this test.";

        public Task<ResearchAnswer> AskAsync(ResearchRequest request, ResearchAnswer? previous, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<ScriptOutput>> WriteAsync(ScriptRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<ScriptPart>> RewriteAsync(ScriptRequest request, IReadOnlyList<Segment> script, string segmentId, string? note,
            string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<StoryboardOutput>> PlanAsync(StoryboardRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<ShotRewriteOutput>> RewriteShotAsync(StoryboardRequest request, IReadOnlyList<Shot> board, IReadOnlyList<CastMember> cast,
            string shotKey, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken) =>
            throw new StageFailedException(Reason);

        public Task<PictureVerdict> CheckAsync(PictureCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken) =>
            throw new StageFailedException(Reason);

        // No sources on the internet in tests: nothing is found, nothing downloads.
        public Task<IReadOnlyList<FoundPicture>> SearchAsync(IReadOnlyList<string> sources, string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FoundPicture>>([]);

        public Task<IReadOnlyList<FoundPicture>> PageAsync(IReadOnlyList<string> sources, string pageUrl, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FoundPicture>>([]);

        public Task<DownloadedPicture?> DownloadAsync(string url, CancellationToken cancellationToken) => Task.FromResult<DownloadedPicture?>(null);

        public Task<string> UploadAsync(string path, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<IReadOnlyList<ComfyFile>> RunAsync(System.Text.Json.Nodes.JsonObject workflow, string name, IProgress<string> progress, CancellationToken cancellationToken) =>
            throw new StageFailedException(Reason);

        public Task ToWavAsync(string source, string target, CancellationToken cancellationToken) => throw new StageFailedException(Reason);
    }

    /// <summary>
    /// Closes the pooled connections to the databases under one test's folder, so the folder can go.
    /// Only those: clearing every pool disposed connections other test classes were using at that
    /// moment. Both forms of the connection string are cleared: the engine's, and the plain
    /// "Data Source=…" tests open themselves.
    /// </summary>
    public static void ClearPool(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }
        foreach (var database in Directory.GetFiles(dataDirectory, "storyforge.db", SearchOption.AllDirectories))
        {
            foreach (var connectionString in new[] { new SqliteConnectionStringBuilder { DataSource = database }.ToString(), $"Data Source={database}" })
            {
                try
                {
                    using var connection = new SqliteConnection(connectionString);
                    SqliteConnection.ClearPool(connection);
                }
                catch (ArgumentException)
                {
                    // A folder like "a;b=c" is no plain connection string: only the engine's form was used.
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            // Remove any secret a test stored in the real credential store.
            var client = host.Services.GetRequiredService<IStoryForgeClient>();
            foreach (var key in Enum.GetValues<SecretKey>())
            {
                client.SetSecretAsync(key, null).GetAwaiter().GetResult();
            }
            // Stopped, not just disposed: a stage the test started (the script after an approved
            // fact sheet, say) must be done writing before the folder goes.
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            host.Dispose();
        }
        ClearPool(DataDirectory);
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
