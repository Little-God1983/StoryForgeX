using System.Diagnostics;
using StoryForge.Engine.Providers;

namespace StoryForge.Engine.Resolve;

/// <summary>Sends a timeline file to Resolve as a new project.</summary>
internal interface IResolveSender
{
    Task<SendToResolveResult> SendAsync(SendToResolveRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the bundled script through Resolve's own interpreter (taken over from RawCutX): one script
/// run per send, and everything that needs Resolve alive happens inside it, including waiting for a
/// cold start, so a failure names the stage it reached.
/// </summary>
internal sealed class ResolveSender(
    ResolveInstall install,
    string scriptPath,
    Func<string, string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> run,
    Func<int>? closeUpdateDialogs = null,
    Func<string, bool>? exists = null,
    Func<bool>? isResolveRunning = null,
    Func<string, Task>? startResolve = null) : IResolveSender
{
    /// <summary>Ten minutes: the import links every clip the timeline names. The script's own 180 s connect retry sits inside it.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan UpdateDialogPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The empty file the script writes beside its request the moment the project exists.</summary>
    public static string CreatedSignalFor(string requestPath) => requestPath + ".created";

    private readonly Func<string, bool> _exists = exists ?? File.Exists;
    private readonly Func<bool> _isResolveRunning = isResolveRunning ?? IsResolveRunning;
    private readonly Func<string, Task> _startResolve = startResolve ?? StartResolve;

    /// <summary>None by default: finding another program's window needs UI Automation, which the app passes in.</summary>
    private readonly Func<int> _closeUpdateDialogs = closeUpdateDialogs ?? (() => 0);

    /// <summary>The real interpreter, through the engine's process runner; its timeout is <see cref="Deadline"/>.</summary>
    public static Func<string, string, CancellationToken, Task<(int, string, string)>> Interpreter(IProcessRunner processes, ResolveInstall install) =>
        async (script, requestPath, cancellationToken) =>
        {
            var result = await processes.RunAsync(install.ResolvePython, $"\"{script}\" \"{requestPath}\"", Deadline, cancellationToken);
            return (result.ExitCode, result.StandardOutput, result.StandardError);
        };

    public async Task<SendToResolveResult> SendAsync(SendToResolveRequest request, CancellationToken cancellationToken)
    {
        if (install.Problem(_exists) is { } problem)
        {
            return new SendToResolveResult(false, "install", problem, null, 0, 0);
        }
        if (!_exists(scriptPath))
        {
            return new SendToResolveResult(false, "install",
                $"The Resolve script was not found at {scriptPath}. It ships beside StoryForge X; a build without it cannot send.", null, 0, 0);
        }
        // Only when it is not up: an open Resolve is never restarted or brought to the front.
        if (!_isResolveRunning())
        {
            try
            {
                await _startResolve(install.ResolveExe);
            }
            catch (Exception ex)
            {
                return new SendToResolveResult(false, "launch", $"Could not start DaVinci Resolve from {install.ResolveExe}: {ex.Message}", null, 0, 0);
            }
        }

        var requestPath = Path.Combine(Path.GetTempPath(), $"storyforgex-send-{Guid.NewGuid():N}.json");
        var createdSignal = CreatedSignalFor(requestPath);
        using var watching = new CancellationTokenSource();
        var watcher = Task.FromResult(0);
        try
        {
            await File.WriteAllTextAsync(requestPath, request.ToJson(), cancellationToken);
            watcher = WatchForUpdateDialogsAsync(createdSignal, watching.Token);
            (int ExitCode, string StdOut, string StdErr) output;
            try
            {
                output = await run(scriptPath, requestPath, cancellationToken);
            }
            finally
            {
                await watching.CancelAsync();
            }
            return SendToResolveResult.Parse(output.ExitCode, output.StdOut, output.StdErr) with { UpdateDialogsClosed = await watcher };
        }
        catch (OperationCanceledException)
        {
            throw;   // a Cancel reads as a cancel, not as a failed send
        }
        catch (TimeoutException)
        {
            return new SendToResolveResult(false, "run",
                $"Resolve did not finish within {Deadline.TotalMinutes:0} minutes and the script was stopped.", null, 0, 0) { UpdateDialogsClosed = await watcher };
        }
        catch (Exception ex)
        {
            return new SendToResolveResult(false, "run", $"Could not run the Resolve script: {ex.Message}", null, 0, 0) { UpdateDialogsClosed = await watcher };
        }
        finally
        {
            // Broad on purpose: this finally may be unwinding a pending return, which an escaping exception would discard.
            try { File.Delete(requestPath); } catch (Exception) { /* best effort */ }
            try { File.Delete(createdSignal); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// Closes Resolve's Software Update window until the script has created the project: while it is
    /// up, CreateProject refuses every name, and Resolve shows it on a cold start (RawCutX, 2026-10-28).
    /// Stops at the script's created signal; a closer that throws is skipped, never fatal.
    /// </summary>
    private async Task<int> WatchForUpdateDialogsAsync(string createdSignal, CancellationToken stop)
    {
        var closed = 0;
        try
        {
            while (!stop.IsCancellationRequested && !File.Exists(createdSignal))
            {
                try
                {
                    closed += await Task.Run(_closeUpdateDialogs, stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The window can vanish mid-close; a missed close only costs the script its retry.
                }
                await Task.Delay(UpdateDialogPoll, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // The send is over, and so is the watching.
        }
        return closed;
    }

    private static bool IsResolveRunning() => Process.GetProcessesByName("Resolve").Length > 0;

    private static Task StartResolve(string exePath)
    {
        // No wait: the script's connect retry absorbs the cold start, and it can see Resolve become answerable.
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
