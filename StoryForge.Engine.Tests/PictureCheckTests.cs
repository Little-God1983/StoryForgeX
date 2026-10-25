using StoryForge.Client;
using StoryForge.Engine.Providers;
using StoryForge.Engine.References;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Tests;

/// <summary>Claude looking at a source picture: what it may read, and what it is asked.</summary>
public sealed class PictureCheckTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public void Without_built_in_tools_a_call_has_none_at_all()
    {
        var arguments = ClaudeCli.Arguments(null, [], "system.md", "{}", "", null);

        Assert.Equal("", arguments[arguments.ToList().IndexOf("--tools") + 1]);
    }

    [Fact]
    public void Looking_at_a_picture_may_read_files_in_its_own_folder_and_nothing_else()
    {
        var arguments = ClaudeCli.Arguments(null, ["Read(./**)"], "system.md", "{}", "", null, builtInTools: ["Read"]);

        Assert.Equal("Read", arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("Read(./**)", arguments[arguments.ToList().IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--mcp-config", arguments);
    }

    [Fact]
    public void The_picture_is_copied_into_the_calls_own_folder_which_is_all_it_may_read()
    {
        var picture = Path.Combine(_data, "pool", "P03.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        File.WriteAllBytes(picture, [1, 2, 3]);
        var request = new PictureCheckRequest(Guid.NewGuid(), "Karlach", CastKind.Character, "", picture, "");

        var call = ClaudeCliPictureCheck.Call(request, _data);

        Assert.Equal(["Read"], call.BuiltInTools);
        Assert.Equal(["Read(./**)"], call.AllowedTools);
        Assert.Null(call.McpConfig);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(call.Folder, "picture.jpg")));
        Assert.Contains("./picture.jpg", call.Prompt);
        Assert.Contains("Karlach", call.Prompt);
    }

    [Fact]
    public void The_answer_schema_passes_through_cmd_unchanged() =>
        CmdShim.RequireSafe("claude.cmd", ClaudeCli.Arguments(null, ["Read(./**)"], "system.md", ClaudeCliPictureCheck.Schema, "", null, builtInTools: ["Read"]));

    [Theory]
    [InlineData(CastKind.Character, "the whole figure, head to feet")]
    [InlineData(CastKind.Place, "the place as a whole")]
    [InlineData(CastKind.Object, "the whole object")]
    public void The_prompt_says_what_whole_means_for_its_kind(CastKind kind, string whole) =>
        Assert.Contains(whole, ClaudeCliPictureCheck.Prompt("Karlach", kind, "", "./picture.png"));
}
