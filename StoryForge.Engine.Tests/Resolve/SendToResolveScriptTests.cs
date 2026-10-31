// Taken over from RawCutX's tests with its Send to Resolve (#71).
using System.Diagnostics;
using StoryForge.Engine.Resolve;

namespace StoryForge.Engine.Tests.Resolve;

/// <summary>
/// The bundled <c>send_to_resolve.py</c>, run for real — under the system Python rather than
/// Resolve's own interpreter, against a fake <c>DaVinciResolveScript</c> module put first on
/// <c>PYTHONPATH</c>. The script imports nothing else outside the standard library, so this is the
/// same code path Resolve runs, minus Resolve. Needs <c>python</c> on PATH, the way the probe tests
/// need ffprobe; the GitHub Windows image has it.
///
/// <para>The fake also replaces <c>time.time</c> and <c>time.sleep</c> with a virtual clock when it
/// is imported, so the script's retry loops can be walked to their deadlines in no real time.</para>
/// </summary>
public sealed class SendToResolveScriptTests
{
    private const string FakeModule = """
        import json
        import os
        import time

        _here = os.path.dirname(os.path.abspath(__file__))
        with open(os.path.join(_here, "scenario.json"), encoding="utf-8") as handle:
            _scenario = json.load(handle)

        _now = [1000.0]
        time.monotonic = lambda: _now[0]
        time.sleep = lambda seconds: _now.__setitem__(0, _now[0] + seconds)
        # The wall clock follows the virtual one - or, for a clock sync during a cold start, runs
        # backwards an hour per second slept, which no retry deadline may be measured against.
        time.time = (lambda: 1e7 - 3600 * _now[0]) if _scenario["clockRunsBackwards"] else (lambda: _now[0])


        def _call(name):
            with open(os.path.join(_here, "calls.txt"), "a", encoding="utf-8") as handle:
                handle.write(name + "\n")


        class _Item:
            def __init__(self, clip):
                self._clip = clip

            # A transition is an item on the track too, but has no media behind it.
            def GetMediaPoolItem(self):
                return object() if self._clip else None


        class _Timeline:
            def GetName(self):
                return "T"

            def GetItemListInTrack(self, kind, index):
                return [_Item(True), _Item(False), _Item(True)]

            def GetSetting(self, key):
                return 60.0

            def AddMarker(self, *args):
                return True


        class _Pool:
            def ImportTimelineFromFile(self, path):
                return _Timeline()


        class _Project:
            def SetSetting(self, key, value):
                return True

            def GetMediaPool(self):
                return _Pool()

            def SetCurrentTimeline(self, timeline):
                return True


        class _Manager:
            def __init__(self):
                # -1 refuses forever, the way Resolve does while a dialog it never gets past is up.
                self._refusals = _scenario["refuseCreates"]

            def CreateProject(self, name):
                _call("CreateProject")
                if self._refusals != 0:
                    if self._refusals > 0:
                        self._refusals -= 1
                    return None
                return _Project()

            def GetProjectListInCurrentFolder(self):
                if _scenario["list"] == "raises":
                    raise RuntimeError("the project list could not be read")
                if _scenario["list"] == "none":
                    return None
                return list(_scenario["existing"])


        class _Resolve:
            def GetProjectManager(self):
                return _Manager()

            def OpenPage(self, page):
                return True


        def scriptapp(name):
            return _Resolve()
        """;

    private static (SendToResolveResult Result, int Creates, bool Signalled) RunScript(
        int refuseCreates, string[]? existing = null, string list = "listed", bool clockRunsBackwards = false)
    {
        existing ??= [];
        var dir = Directory.CreateTempSubdirectory("rawcutx-script-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "DaVinciResolveScript.py"), FakeModule);
            File.WriteAllText(Path.Combine(dir.FullName, "scenario.json"),
                System.Text.Json.JsonSerializer.Serialize(new { refuseCreates, existing, list, clockRunsBackwards }));

            var requestPath = Path.Combine(dir.FullName, "request.json");
            File.WriteAllText(requestPath, new SendToResolveRequest(
                "Proj", @"D:\clips\timeline.fcpxml", "60", true,
                [new ResolveMarker(45, "Yellow", "mid edits")]).ToJson());

            var start = new ProcessStartInfo("python")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Resolve", "send_to_resolve.py"));
            start.ArgumentList.Add(requestPath);
            start.Environment["PYTHONPATH"] = dir.FullName;

            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                // A retry loop that never ends must fail the test, not hang the run.
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("the script did not finish within 30 s");
            }

            var stdout = stdoutTask.Result;

            var calls = Path.Combine(dir.FullName, "calls.txt");
            var creates = File.Exists(calls)
                ? File.ReadAllLines(calls).Count(l => l == "CreateProject")
                : 0;

            return (SendToResolveResult.Parse(process.ExitCode, stdout, stderr.Result), creates,
                File.Exists(ResolveSender.CreatedSignalFor(requestPath)));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AFreeName_IsCreatedFirstTime()
    {
        var (result, creates, _) = RunScript(refuseCreates: 0);

        Assert.True(result.Ok, result.Problem);
        Assert.Equal("done", result.Stage);
        Assert.Equal(1, creates);
    }

    /// <summary>Found in the first send from StoryForge X (#71): Resolve lists the cross dissolves
    /// on the video track among the clips, and "17 clips" came back for a cut of 11.</summary>
    [Fact]
    public void TheClipCount_LeavesTheTransitionsOut()
    {
        var (result, _, _) = RunScript(refuseCreates: 0);

        Assert.Equal(2, result.VideoItems);
    }

    /// <summary>The signal is what stops RawCutX closing Resolve's update windows: once the project
    /// exists, one the user opens during the import is theirs to keep.</summary>
    [Fact]
    public void ACreatedProject_IsSignalledToRawCutX()
    {
        var (_, _, signalled) = RunScript(refuseCreates: 0);

        Assert.True(signalled);
    }

    /// <summary>Whether Resolve counts 'proj ' and 'Proj' as one name is unknown, so a near-match
    /// is no proof of a clash: the update dialog may still be what refused it. It does not stop the
    /// retry — it only names the near-match once the retry has run out.</summary>
    [Fact]
    public void ANearMatch_IsRetried_ThenNamedInTheMessage()
    {
        var (result, creates, _) = RunScript(refuseCreates: 2, existing: ["proj "]);

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(3, creates);

        (result, _, _) = RunScript(refuseCreates: -1, existing: ["proj "]);

        Assert.False(result.Ok);
        Assert.Contains("'proj '", result.Problem!, StringComparison.Ordinal);
        Assert.DoesNotContain("no project by that name exists", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AWallClockThatJumps_DoesNotStretchTheRetry()
    {
        var (result, creates, _) = RunScript(refuseCreates: -1, clockRunsBackwards: true);

        Assert.False(result.Ok);
        Assert.InRange(creates, 2, 60);
    }

    /// <summary>A list that cannot be read proves nothing either way, so the final message must not
    /// claim the name is free — the old message's own rule: a clash asserted (or denied) without
    /// evidence sends the user hunting.</summary>
    /// <summary>Resolve's API mostly fails by returning None rather than raising; both are an
    /// unread list.</summary>
    [Theory]
    [InlineData("raises")]
    [InlineData("none")]
    public void AnUnreadableProjectList_IsNotPassedOffAsProofTheNameIsFree(string list)
    {
        var (result, _, _) = RunScript(refuseCreates: -1, list: list);

        Assert.False(result.Ok);
        Assert.Contains("may already be taken", result.Problem!, StringComparison.Ordinal);
        Assert.DoesNotContain("no project by that name exists", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>The bug: Resolve's Software Update window, up on a cold start, makes CreateProject
    /// return None for a name nobody has. The send used to give up on the first refusal and blame
    /// the name; RawCutX now closes that window, and the script keeps asking until it has.</summary>
    [Fact]
    public void ARefusalWhileTheNameIsFree_IsRetriedUntilResolveCreatesIt()
    {
        var (result, creates, _) = RunScript(refuseCreates: 3);

        Assert.True(result.Ok, result.Problem);
        Assert.Equal("done", result.Stage);
        Assert.Equal(4, creates);
    }

    [Fact]
    public void ATakenName_IsReportedAsTaken_WithoutRetrying()
    {
        var (result, creates, _) = RunScript(refuseCreates: -1, existing: ["Other", "Proj"]);

        Assert.False(result.Ok);
        Assert.Equal("create", result.Stage);
        Assert.Contains("already a project called 'Proj'", result.Problem!, StringComparison.Ordinal);
        Assert.Equal(1, creates);
    }

    [Fact]
    public void ARefusalThatNeverLifts_GivesUpAndPointsAtADialog()
    {
        var (result, creates, signalled) = RunScript(refuseCreates: -1);

        Assert.False(result.Ok);
        Assert.Equal("create", result.Stage);
        Assert.Contains("Software Update", result.Problem!, StringComparison.Ordinal);
        Assert.True(creates > 1, $"expected retries, saw {creates} CreateProject call(s)");
        Assert.False(signalled, "nothing was created, so nothing may be signalled");
    }
}
