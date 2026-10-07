using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StoryForge.ResearchServer;

// StoryForge.ResearchServer --source bg3.wiki --source forgottenrealms.fandom.com
// Started by Claude CLI over stdio for one Research run; stdout carries the MCP protocol only.
var sources = new SourceList(SourcesFrom(args));
if (sources.Sources.Count == 0)
{
    Console.Error.WriteLine("Usage: StoryForge.ResearchServer --source <site> [--source <site> ...]");
    return 2;
}

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddSingleton(sources);
builder.Services.AddSingleton(_ => new SiteWeb(SiteWeb.CreateHttp(), sources));
builder.Services.AddSingleton<MediaWiki>();
builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "storyforge", Version = "1" })
    .WithStdioServerTransport()
    .WithTools<ResearchTools>();
await builder.Build().RunAsync();
return 0;

static IEnumerable<string> SourcesFrom(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--source")
        {
            yield return args[++i];
        }
    }
}
