using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;

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
        // No test ever reaches the real Claude CLI: the models refuse unless a test puts its own in.
        builder.Services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new NoModel()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(new NoModel()));
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

    /// <summary>A model that is not there: every stage that asks it fails with a plain reason.</summary>
    private sealed class NoModel : IResearchAgent, IScriptAgent
    {
        private const string Reason = "No model in this test.";

        public Task<ResearchAnswer> AskAsync(ResearchRequest request, ResearchAnswer? previous, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<ScriptOutput>> WriteAsync(ScriptRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);

        public Task<ScriptAnswer<ScriptPart>> RewriteAsync(ScriptRequest request, IReadOnlyList<Segment> script, string segmentId, string? session,
            IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken) => throw new StageFailedException(Reason);
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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
