using System.Diagnostics;

namespace StoryForge.Engine.Providers;

internal sealed record StreamingRunResult(int ExitCode, string StandardError);

/// <summary>Runs a long CLI job and hands over its output line by line as it comes (Claude CLI's stream-json).</summary>
internal interface IStreamingProcess
{
    /// <param name="input">Written to the process's standard input, which is then closed.</param>
    /// <exception cref="ExecutableNotFoundException">The executable is neither a file nor on PATH.</exception>
    /// <exception cref="TimeoutException">The process ran longer than <paramref name="timeout"/> and was stopped.</exception>
    Task<StreamingRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string input,
        Action<string> onLine,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class StreamingProcess : IStreamingProcess
{
    public async Task<StreamingRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string input,
        Action<string> onLine,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resolved = ExecutableResolver.Resolve(
                executable, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"))
            ?? throw new ExecutableNotFoundException(executable);
        CmdShim.RequireSafe(resolved, arguments);

        var start = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It quit before reading its input (not logged in, a bad flag): its exit code and
                // error output below say why.
            }
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                onLine(line);
            }
            await process.WaitForExitAsync(deadline.Token);
            return new StreamingRunResult(process.ExitCode, await error);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{executable}' did not finish within {timeout.TotalMinutes:0.#} min.");
        }
        catch
        {
            // Whatever went wrong on our side (a line we could not read, a broken pipe), the job
            // must not run on unseen, spending tokens.
            KillTree(process);
            throw;
        }
    }

    /// <summary>The whole tree: claude.cmd runs node, and node the research server.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// A .cmd or .bat (npm's claude.cmd) runs through cmd.exe, which reads %, ^, &amp;, |, &lt; and &gt; in
/// its command line as its own. Arguments with them would arrive changed or run something else, so
/// they are refused; long texts go through a file or standard input instead.
/// </summary>
internal static class CmdShim
{
    private static readonly char[] Special = ['%', '^', '&', '|', '<', '>', '!', '\r', '\n'];

    public static void RequireSafe(string resolvedExecutable, IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(resolvedExecutable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var unsafeArgument = arguments.FirstOrDefault(a => a.IndexOfAny(Special) >= 0);
        if (unsafeArgument is not null)
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(resolvedExecutable)}' runs through cmd.exe, which would change this argument: {unsafeArgument}");
        }
    }
}
