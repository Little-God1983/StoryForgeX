using StoryForge.Engine.Providers;

namespace StoryForge.Engine.Tests;

/// <summary>Runs real processes: a fake CLI written as a .cmd file, like npm's claude.cmd shim.</summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("StoryForgeX.Tests.").FullName;
    private readonly ProcessRunner _runner = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string WriteCmd(string name, string body)
    {
        var path = Path.Combine(_folder, name + ".cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    [Fact]
    public void A_bare_name_resolves_to_a_cmd_file_on_the_path_through_pathext()
    {
        var cmd = WriteCmd("fakecli", "echo hi");

        var resolved = ExecutableResolver.Resolve("fakecli", pathVariable: _folder, pathExt: ".COM;.EXE;.BAT;.CMD");

        Assert.Equal(cmd, resolved, ignoreCase: true);
    }

    [Fact]
    public void A_name_that_is_nowhere_on_the_path_does_not_resolve()
    {
        Assert.Null(ExecutableResolver.Resolve("no-such-tool-" + Guid.NewGuid().ToString("N"), _folder, ".EXE;.CMD"));
    }

    [Fact]
    public void A_full_path_resolves_to_itself_when_the_file_exists()
    {
        var cmd = WriteCmd("direct", "echo hi");

        Assert.Equal(cmd, ExecutableResolver.Resolve(cmd, pathVariable: "", pathExt: ".EXE"));
    }

    [Fact]
    public async Task A_cmd_file_runs_and_its_output_comes_back()
    {
        var cmd = WriteCmd("versioned", "echo 9.9.9 (Fake CLI) %1");

        var result = await _runner.RunAsync(cmd, "--version", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("9.9.9 (Fake CLI) --version", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task A_failing_process_reports_its_exit_code_and_error_output()
    {
        var cmd = WriteCmd("broken", "echo bad flag 1>&2\r\nexit /b 3");

        var result = await _runner.RunAsync(cmd, "", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("bad flag", result.StandardError.Trim());
    }

    [Fact]
    public async Task An_unknown_executable_throws_not_found()
    {
        var missing = "no-such-tool-" + Guid.NewGuid().ToString("N");

        var error = await Assert.ThrowsAsync<ExecutableNotFoundException>(
            () => _runner.RunAsync(missing, "", TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal(missing, error.Executable);
    }

    [Fact]
    public async Task A_child_that_keeps_the_output_open_after_the_process_exits_still_times_out()
    {
        // The .cmd exits at once, but the background ping inherits its stdout and holds it open.
        var cmd = WriteCmd("leaky", "start /b ping -n 30 127.0.0.1\r\necho started");
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.RunAsync(cmd, "", TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_quoted_path_entry_is_searched_without_its_quotes()
    {
        var cmd = WriteCmd("quotedcli", "echo hi");

        var resolved = ExecutableResolver.Resolve("quotedcli", pathVariable: $"\"{_folder}\"", pathExt: ".CMD");

        Assert.Equal(cmd, resolved, ignoreCase: true);
    }

    [Fact]
    public async Task A_process_that_runs_too_long_is_stopped_and_reported_as_timed_out()
    {
        var cmd = WriteCmd("slow", "ping -n 30 127.0.0.1 >nul");
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.RunAsync(cmd, "", TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
