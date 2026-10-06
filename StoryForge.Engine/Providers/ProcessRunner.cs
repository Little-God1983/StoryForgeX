using System.Diagnostics;

namespace StoryForge.Engine.Providers;

internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ExecutableNotFoundException(string executable)
    : Exception($"'{executable}' was not found.")
{
    public string Executable { get; } = executable;
}

internal interface IProcessRunner
{
    /// <exception cref="ExecutableNotFoundException">The executable is neither a file nor on PATH.</exception>
    /// <exception cref="TimeoutException">The process ran longer than <paramref name="timeout"/> and was stopped.</exception>
    Task<ProcessRunResult> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var resolved = ExecutableResolver.Resolve(
                executable, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"))
            ?? throw new ExecutableNotFoundException(executable);

        using var process = new Process { StartInfo = StartInfo(resolved, arguments) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // The whole tree: a .cmd shim runs the real tool as a child of cmd.exe.
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{executable}' did not finish within {timeout.TotalSeconds:0} s.");
        }

        return new ProcessRunResult(process.ExitCode, await output, await error);
    }

    // A resolved .cmd path (npm's claude.cmd) starts fine this way; what Process.Start can't do is
    // find "claude" → "claude.cmd" on its own, hence ExecutableResolver.
    private static ProcessStartInfo StartInfo(string resolved, string arguments) => new(resolved, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
}

/// <summary>
/// Finds an executable the way a shell does: a path is taken as is, a bare name is looked up in
/// every PATH folder, and on Windows with each PATHEXT extension ("claude" → "claude.cmd").
/// </summary>
internal static class ExecutableResolver
{
    public static string? Resolve(string executable, string? pathVariable, string? pathExt)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (pathExt ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return Candidates(executable, extensions).FirstOrDefault(File.Exists);
        }

        return (pathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(folder => Candidates(Path.Combine(folder, executable), extensions))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Candidates(string path, string[] extensions)
    {
        if (Path.HasExtension(path))
        {
            yield return path;
        }
        foreach (var extension in extensions)
        {
            yield return path + extension;
        }
        if (!OperatingSystem.IsWindows())
        {
            yield return path;
        }
    }
}
