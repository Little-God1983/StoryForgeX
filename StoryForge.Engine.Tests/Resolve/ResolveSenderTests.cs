// Taken over from RawCutX's tests with its Send to Resolve (#71).
using StoryForge.Engine.Resolve;

namespace StoryForge.Engine.Tests.Resolve;

/// <summary>
/// The sender, tested against its process seam rather than against Resolve. What is pinned here is
/// everything the app is responsible for: refusing early when the install is not there, starting
/// Resolve only when it is not already running, writing the request where the script can read it,
/// and turning whatever comes back into a result.
/// </summary>
public sealed class ResolveSenderTests
{
    private static SendToResolveRequest ARequest() =>
        new("Proj", @"D:\clips\timeline.fcpxml", "60", true,
            [new ResolveMarker(45, "Yellow", "mid edits: 8 breath lifts")]);

    private const string Success =
        """{"ok":true,"stage":"done","problem":null,"timelineName":"T","videoItems":5,"markersPlaced":1}""";

    [Fact]
    public async Task AMissingInterpreter_FailsBeforeAnyProcessIsStarted()
    {
        var started = false;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => false,
            isResolveRunning: () => true,
            startResolve: _ => { started = true; return Task.CompletedTask; },
            run: (_, _, _) => throw new InvalidOperationException("must not run"));

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("install", result.Stage);
        Assert.False(started);
    }

    [Fact]
    public async Task AnAlreadyRunningResolve_IsNeverStartedAgain()
    {
        var started = false;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => { started = true; return Task.CompletedTask; },
            run: (_, _, _) => Task.FromResult((0, Success, "")));

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(started);
    }

    [Fact]
    public async Task AClosedResolve_IsStarted()
    {
        var startedWith = "";
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => false,
            startResolve: path => { startedWith = path; return Task.CompletedTask; },
            run: (_, _, _) => Task.FromResult((0, Success, "")));

        await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.Equal(ResolveInstall.DefaultResolveExe, startedWith);
    }

    [Fact]
    public async Task TheRequestIsWrittenWhereTheScriptIsToldToLookForIt()
    {
        string? requestPath = null;
        string? contents = null;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (script, argument, _) =>
            {
                requestPath = argument;
                contents = File.ReadAllText(argument);
                Assert.Equal(@"C:\app\send_to_resolve.py", script);
                return Task.FromResult((0, Success, ""));
            });

        await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.NotNull(requestPath);
        Assert.Contains("\"frameRate\":\"60\"", contents!, StringComparison.Ordinal);
        Assert.False(File.Exists(requestPath!), "the temp request must be cleaned up");
    }

    [Fact]
    public async Task AProcessThatThrows_BecomesAReportedFailure()
    {
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (_, _, _) => throw new InvalidOperationException("the interpreter died"));

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("the interpreter died", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProcessThatThrows_CleansUpTheTempRequestFile()
    {
        string? requestPath = null;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (_, argument, _) =>
            {
                requestPath = argument;
                throw new InvalidOperationException("the interpreter died");
            });

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(requestPath);
        Assert.False(File.Exists(requestPath!), "the temp request must be cleaned up even when run throws");
    }

    /// <summary>A cancelled token has to read as a cancel everywhere in this app, not as a reported
    /// failure — see the <c>catch (OperationCanceledException)</c> ahead of the general catch in
    /// <c>ResolveSender.SendAsync</c>, which exists to rethrow rather than report it.
    ///
    /// <para>The token is cancelled up front, rather than having the <c>run</c> seam throw
    /// <see cref="OperationCanceledException"/> directly: <c>ProcessDeadline.RunAsync</c>'s own catch
    /// filter checks whether the CALLER's token tripped, so an OCE thrown while that token is still
    /// live gets converted to a <see cref="TimeoutException"/> instead of propagating — an actually
    /// cancelled token is what triggers the real rethrow path this test pins.</para></summary>
    [Fact]
    public async Task ACancelledToken_ThrowsRatherThanBeingReported()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ranScript = false;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (_, _, _) => { ranScript = true; return Task.FromResult((0, Success, "")); });

        // ThrowsAnyAsync, not ThrowsAsync: File.WriteAllTextAsync raises the TaskCanceledException
        // subclass rather than the base type, and the catch clause under test (and this assertion)
        // must accept either — a real cancel button is not fussy about which one it gets.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(ARequest(), cts.Token));

        Assert.False(ranScript, "a cancelled send must not reach the interpreter");
    }

    /// <summary>The neighbouring case that must NOT throw: <c>ProcessDeadline.RunAsync</c> converts its
    /// own ten-minute deadline expiry into a <see cref="TimeoutException"/>, which is a different type
    /// from <see cref="OperationCanceledException"/> and so must fall into the general catch and come
    /// back as a result the user can read.</summary>
    [Fact]
    public async Task ADeadlineTimeout_BecomesAReportedFailureWithAReadableMessage()
    {
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (_, _, _) => throw new TimeoutException(
                "'send_to_resolve.py' was still running after 10 minute(s) and was stopped — the process looked wedged."));

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("run", result.Stage);
        Assert.Contains("Resolve did not finish within 10 minutes", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>Resolve's Software Update window makes CreateProject refuse every name; the sender
    /// watches for it for as long as the script runs. The script waits here until the watcher has
    /// had its first look, so the test does not depend on how fast either side gets going.</summary>
    [Fact]
    public async Task AnUpdateDialogClosedDuringTheSend_IsCountedInTheResult()
    {
        var looked = new TaskCompletionSource();
        var calls = 0;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: async (_, _, _) =>
            {
                await looked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return (0, Success, "");
            },
            closeUpdateDialogs: () =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                looked.TrySetResult();
                return first ? 1 : 0;
            });

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, result.UpdateDialogsClosed);
    }

    [Fact]
    public async Task ACloserThatThrows_DoesNotFailTheSend()
    {
        var looked = new TaskCompletionSource();
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: async (_, _, _) =>
            {
                await looked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return (0, Success, "");
            },
            closeUpdateDialogs: () =>
            {
                looked.TrySetResult();
                throw new InvalidOperationException("the window went away mid-close");
            });

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.UpdateDialogsClosed);
    }

    /// <summary>The watcher belongs to the send: once the result is back, it must stop touching
    /// Resolve's windows — the user may open that dialog on purpose afterwards.</summary>
    [Fact]
    public async Task TheWatcherStops_WhenTheSendReturns()
    {
        var calls = 0;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: (_, _, _) => Task.FromResult((0, Success, "")),
            closeUpdateDialogs: () => { Interlocked.Increment(ref calls); return 0; });

        await sender.SendAsync(ARequest(), CancellationToken.None);
        var atReturn = Volatile.Read(ref calls);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(atReturn, Volatile.Read(ref calls));
    }

    /// <summary>The window only matters until the project exists; one the user opens during a
    /// long import is theirs. The script's created-signal ends the watching while it still runs.</summary>
    [Fact]
    public async Task TheWatcherStops_OnceTheScriptSignalsTheProjectIsCreated()
    {
        var looked = new TaskCompletionSource();
        var calls = 0;
        int? afterSignal = null;
        int? later = null;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: async (_, requestPath, _) =>
            {
                await looked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await File.WriteAllTextAsync(ResolveSender.CreatedSignalFor(requestPath), "");
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                afterSignal = Volatile.Read(ref calls);
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                later = Volatile.Read(ref calls);
                return (0, Success, "");
            },
            closeUpdateDialogs: () =>
            {
                Interlocked.Increment(ref calls);
                looked.TrySetResult();
                return 0;
            });

        await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.Equal(afterSignal, later);
    }

    [Fact]
    public async Task TheCreatedSignal_IsCleanedUp()
    {
        string? signal = null;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: async (_, requestPath, _) =>
            {
                signal = ResolveSender.CreatedSignalFor(requestPath);
                await File.WriteAllTextAsync(signal, "");
                return (0, Success, "");
            });

        await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.False(File.Exists(signal!), "the created-signal must be cleaned up with the request");
    }

    /// <summary>A window closed in someone else's app is never silent — not even when the send then
    /// fails outright, the deadline's TimeoutException included.</summary>
    [Fact]
    public async Task AFailedScript_StillReportsTheDialogsItClosed()
    {
        var looked = new TaskCompletionSource();
        var calls = 0;
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => true,
            startResolve: _ => Task.CompletedTask,
            run: async (_, _, _) =>
            {
                await looked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                throw new TimeoutException("the process looked wedged");
            },
            closeUpdateDialogs: () =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                looked.TrySetResult();
                return first ? 1 : 0;
            });

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, result.UpdateDialogsClosed);
    }

    [Fact]
    public async Task AFailedLaunch_IsAReportedFailure_AndTheScriptIsNeverRun()
    {
        var sender = new ResolveSender(
            ResolveInstall.Locate(null, null), @"C:\app\send_to_resolve.py",
            exists: _ => true,
            isResolveRunning: () => false,
            startResolve: _ => throw new InvalidOperationException("access denied"),
            run: (_, _, _) => throw new InvalidOperationException("must not run"));

        var result = await sender.SendAsync(ARequest(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("launch", result.Stage);
        Assert.Contains(ResolveInstall.DefaultResolveExe, result.Problem!, StringComparison.Ordinal);
        Assert.Contains("access denied", result.Problem!, StringComparison.Ordinal);
    }
}
