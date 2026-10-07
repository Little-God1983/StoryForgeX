using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StoryForge.App.Tests;

/// <summary>
/// scripts\_common.ps1, build.ps1 and build.cmd: the stable build that bumps the version, publishes
/// into E:\StableVersion and pushes main. They are run in a real pwsh and a real cmd, because the
/// rules worth pinning - which version comes next, which folder may be deleted - live in PowerShell,
/// not in C#. The helpers are dot-sourced the way build.ps1 does it; build.ps1 itself runs in a
/// <see cref="FakeRepo"/>.
/// </summary>
public sealed class BuildScriptTests
{
    private const string PauseLine = "Press any key to continue";

    [Theory]
    [InlineData("1.2.2.8", "build", "1.2.2.9")]
    [InlineData("1.2.2", "build", "1.2.2.1")]
    [InlineData("1.2.2.8", "patch", "1.2.3")]
    [InlineData("1.2.2.8", "minor", "1.3.0")]
    [InlineData("1.2.2.8", "major", "2.0.0")]
    public void TheNextVersion_ZeroesEverythingRightOfThePart(string current, string part, string next)
    {
        var result = Pwsh.RunCommon($"Get-NextVersion '{current}' '{part}'");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(next, result.Output.Trim());
    }

    [Fact]
    public void TheVersionWriteBack_ChangesTheNumberAndNothingElse()
    {
        using var temp = new TempFolder();
        var props = Path.Combine(temp.Path, "Directory.Build.props");
        var before = "<Project>\r\n  <!-- 1.0.0 in a comment -->\r\n  <PropertyGroup>\n    <Version>1.2.2.8</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        File.WriteAllBytes(props, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(before)]);

        var result = Pwsh.RunCommon($"$script:PropsPath = '{props}'; Set-PropsVersion '1.2.2.9'");

        Assert.Equal(0, result.ExitCode);
        var expected = before.Replace("<Version>1.2.2.8</Version>", "<Version>1.2.2.9</Version>");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(props));
    }

    [Theory]
    [InlineData(@"root\StoryForgeX-1.2.2.8", true)]
    [InlineData(@"root", false)]
    [InlineData(@"rootOld\StoryForgeX", false)]
    [InlineData(@"builds\StoryForgeX", false)]
    public void APathIsUnderARoot_OnlyBelowAFolderBoundary(string path, bool under)
    {
        // In a folder of the test's own: the check opens the folders it is asked about, and the real
        // E:\StableVersion is not this test's to open.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "rootOld"));

        var result = Pwsh.RunCommon($"Test-PathUnder '{Path.Combine(temp.Path, path)}' '{root}'");

        Assert.Equal(under.ToString(), result.Output.Trim(), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"alias\StoryForgeX-1.2.2.9", true)]
    [InlineData(@"alias", true)]
    [InlineData(@"root\StoryForgeX-1.2.2.9", true)]
    [InlineData(@"rootOld\StoryForgeX-1.2.2.9", false)]
    public void APathThroughAnAliasOfTheRoot_IsStillUnderTheRoot(string path, bool under)
    {
        // E:\STABLE~1\..., a junction or a subst drive leads into E:\StableVersion without matching
        // it as a string. The folder asked about does not exist yet - a new one-off build's target.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "alias")}' -Target '{root}' | Out-Null; " +
            $"Test-PathUnder '{Path.Combine(temp.Path, path)}' '{root}' -OrEqual");

        Assert.Equal(under.ToString(), result.Output.Trim(), ignoreCase: true);
    }

    [Fact]
    public void OnlyAFolderWithOurMarker_IsOurs()
    {
        using var temp = new TempFolder();
        var ours = Directory.CreateDirectory(Path.Combine(temp.Path, "ours")).FullName;
        var foreign = Directory.CreateDirectory(Path.Combine(temp.Path, "foreign")).FullName;
        var unmarked = Directory.CreateDirectory(Path.Combine(temp.Path, "unmarked")).FullName;
        File.WriteAllText(Path.Combine(foreign, "storyforgex-install.json"), """{ "app": "CodeSwitchX" }""");
        File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "x");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{ours}' '1.2.2.9'; " +
            $"(Test-OurInstall '{ours}'), (Test-OurInstall '{foreign}'), (Test-OurInstall '{unmarked}') -join ','");

        Assert.Equal("True,False,False", result.Output.Trim());
    }

    [Theory]
    [InlineData("0123456")]
    [InlineData("1234567")]
    [InlineData("f31a120")]
    [InlineData("")]
    public void AOneOffStamp_IsNeverANumericPrereleaseIdentifier(string commit)
    {
        // NuGet's restore rejects a SemVer prerelease identifier that is all digits with a leading
        // zero: -p:Version=1.2.2.8-oneoff.0123456 fails with MSB4181, about 1 commit in 270. The
        // rule itself is checked, not the spelling: every dot-separated part after the dash is a
        // number without a leading zero, or has a letter or a dash in it.
        var result = Pwsh.RunCommon($"Get-OneOffVersion '1.2.2.8' '{commit}'");

        var stamp = result.Output.Trim();
        Assert.StartsWith("1.2.2.8-oneoff.", stamp);
        Assert.EndsWith(commit, stamp);
        Assert.All(
            stamp[(stamp.IndexOf('-') + 1)..].Split('.'),
            identifier => Assert.Matches(@"^(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)$", identifier));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(128, 1)]
    public void AFailingGitStatus_IsNotACleanTree(int statusExitCode, int expectedExitCode)
    {
        // A corrupt index or a safe.directory refusal makes git status print nothing to stdout and
        // exit non-zero. Empty output on its own reads as "no changes", so the exit code has to be
        // what decides.
        var fakeGit =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
            "$global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'rev-parse' { 'main' } " +
            $"'status' {{ $global:LASTEXITCODE = {statusExitCode} }} " +
            "'rev-list' { \"0`t0\" } } }";

        var result = Pwsh.RunCommon($"{fakeGit}; Assert-ReleaseReady");

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (expectedExitCode != 0)
        {
            Assert.Contains("git status failed", result.Output);
        }
    }

    [Theory]
    [InlineData("chore: bump version to 1.2.2.9", "'Directory.Build.props'", true)]
    [InlineData("chore: bump version to 1.2.2.9", "'Directory.Build.props', 'src/Program.cs'", false)]
    [InlineData("feat: something else", "'Directory.Build.props'", false)]
    public void AnUnpushedBump_IsToBePushed_NotResetAway(string subject, string files, bool isTheBump)
    {
        // After a rejected push the bump commit is the one commit main is ahead by. Its build is
        // already installed, so "reset --hard origin/main" - right for any other stray commit - is
        // the advice that hands the same version number out twice.
        var fakeGit =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
            "$global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'rev-parse' { 'main' } 'rev-list' { \"1`t0\" } " +
            $"'log' {{ 'abc1234 {subject}' }} 'show' {{ {files} }} }} }}";

        var result = Pwsh.RunCommon($"{fakeGit}; Assert-ReleaseReady");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(isTheBump, result.Output.Contains("was never pushed"));
        Assert.Equal(!isTheBump, result.Output.Contains("reset --hard"));
    }

    [Fact]
    public void AnUnpushedBump_WithOtherCommitsBesideIt_IsStillKept()
    {
        // The push was rejected, and one more commit was made on main before the next run. A plain
        // "reset --hard origin/main" would take the bump away together with that commit.
        var fakeGit =
            "function Invoke-Git { param([string[]]$Arguments, [switch]$Quiet) " +
            "$global:LASTEXITCODE = 0; " +
            "switch ($Arguments[0]) { 'rev-parse' { 'main' } 'rev-list' { \"2`t0\" } " +
            "'log' { 'def5678 fix: one more thing'; 'abc1234 chore: bump version to 1.2.2.9' } " +
            "'show' { 'Directory.Build.props' } } }";

        var result = Pwsh.RunCommon($"{fakeGit}; Assert-ReleaseReady");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("other commits sit beside it", result.Output);
        Assert.Contains("git cherry-pick abc1234", result.Output);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AFinishedStableFolder_IsNeverRepublishedOver(bool complete, int expectedExitCode)
    {
        // The junction and the Start Menu shortcut move to the new folder before the bump is
        // committed and pushed. A bump that is then lost hands out the same number again - and its
        // folder is then the one the shortcut opens, possibly running.
        // A folder an interrupted publish left half-done was never made current, so it may go.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "StoryForgeX-1.2.2.9");
        var marker = complete ? "-Complete" : "";

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '1.2.2.9' {marker}; Assert-StableTargetFree '{folder}' '1.2.2.9' '1.2.2.8'");

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (complete)
        {
            Assert.Contains("already published", result.Output);
        }
    }

    [Fact]
    public void TheFolderTheCurrentLinkPointsAt_IsNeverRepublishedOver_WhateverItsMarkerSays()
    {
        // The link moves first and the marker is written second. A run that dies between the two
        // leaves the live folder without its finished mark; the link itself still says it is live.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "StoryForgeX-1.2.2.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '1.2.2.9'; " +
            $"New-Item -ItemType Junction -Path '{Path.Combine(temp.Path, "StoryForgeX")}' -Target '{folder}' | Out-Null; " +
            $"Assert-StableTargetFree '{folder}' '1.2.2.9' '1.2.2.8'");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already published", result.Output);
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("\"false\"", 0)]
    public void OnlyARealTrue_MarksAFolderFinished(string completeJson, int expectedExitCode)
    {
        // A marker edited by hand to "complete": "false" is a non-empty string, which a plain
        // [bool] cast reads as true.
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "StoryForgeX-1.2.2.9")).FullName;
        File.WriteAllText(
            Path.Combine(folder, "storyforgex-install.json"),
            $$"""{ "app": "StoryForgeX", "version": "1.2.2.9", "complete": {{completeJson}} }""");

        var result = Pwsh.RunCommon($"Assert-StableTargetFree '{folder}' '1.2.2.9' '1.2.2.8'");

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("1.2.2.8", false)]
    [InlineData("1.2.2.8-oneoff.gf31a120", true)]
    public void AOneOffBuild_OnlyReusesAOneOffFolder(string markedVersion, bool reusable)
    {
        // A second line behind the path check: a stable release is never a one-off's folder.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "StoryForgeX-test");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '{markedVersion}' -Complete; Test-OurInstall '{folder}' -OneOff");

        Assert.Equal(reusable.ToString(), result.Output.Trim(), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"\\localhost\")]
    [InlineData(@"\\127.0.0.1\")]
    [InlineData(@"\\?\UNC\localhost\")]
    public void APathThroughAShareToThisPc_IsStillUnderTheRoot(string server)
    {
        // \\localhost\E$\StableVersion\... is E:\StableVersion\..., but Windows keeps a share path a
        // share path, so no spelling of it ever starts with the root. Which folder it is decides.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "rootOld"));
        var share = server + temp.Path[0] + "$" + temp.Path[2..];
        if (!Directory.Exists(share))
        {
            return; // No admin share to this drive for this user: nothing to ask.
        }

        var result = Pwsh.RunCommon(
            $"Test-PathUnder '{share}\\root\\StoryForgeX-1.2.2.9' '{root}' -OrEqual; " +
            $"Test-PathUnder '{share}\\root' '{root}' -OrEqual; " +
            $"Test-PathUnder '{share}\\root' '{root}'; " +
            $"Test-PathUnder '{share}\\rootOld\\StoryForgeX-1.2.2.9' '{root}' -OrEqual");

        Assert.Equal(["True", "True", "False", "False"], Lines(result.Output));
    }

    [Fact]
    public void TheFolderTheCurrentLinkPointsAt_IsCurrent_HoweverTheLinkSpellsIt()
    {
        // A link remade by hand through another spelling of the root still points at the same
        // folder. "alias" stands in for E:\STABLE~1 or a subst drive.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "StoryForgeX-1.2.2.9")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "StoryForgeX-1.2.2.8")).FullName;
        var alias = Path.Combine(temp.Path, "alias");

        var result = Pwsh.RunCommon(
            $"New-Item -ItemType Junction -Path '{alias}' -Target '{root}' | Out-Null; " +
            $"New-Item -ItemType Junction -Path '{Path.Combine(root, "StoryForgeX")}' -Target '{alias}\\StoryForgeX-1.2.2.9' | Out-Null; " +
            $"Test-InstallCurrent '{folder}'; Test-InstallCurrent '{other}'");

        Assert.Equal(["True", "False"], Lines(result.Output));
    }

    [Fact]
    public void ARunningApp_IsFoundByItsRealFolder_HoweverItWasStarted()
    {
        // The real lookup, with a stand-in that keeps running: a copy of ping.exe under the app's
        // name. It is started through the current link, the way the Start Menu starts StoryForgeX, and
        // Windows then reports the link's path - not the version folder's - as where it runs from.
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "StoryForgeX-1.2.2.9")).FullName;
        var neighbour = Directory.CreateDirectory(Path.Combine(root, "StoryForgeX-1.2.2.8")).FullName;
        var link = Path.Combine(root, "StoryForgeX");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Path.Combine(folder, "StoryForge.App.exe"));
        Assert.Equal(0, Pwsh.Run($"New-Item -ItemType Junction -Path '{link}' -Target '{folder}' | Out-Null").ExitCode);

        var start = new ProcessStartInfo(Path.Combine(link, "StoryForge.App.exe"), "-n 120 127.0.0.1")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var app = Process.Start(start)!;
        try
        {
            var result = Pwsh.RunCommon(
                $"(Get-AppProcess '{folder}').ProcessId; 'by folder'; " +
                $"(Get-AppProcess -AnyVersion -InstallRoot '{root}').ProcessId; 'by root'; " +
                $"(Get-AppProcess '{neighbour}').ProcessId; 'neighbour'; " +
                $"(Get-AppProcess -AnyVersion -InstallRoot '{temp.Path}').ProcessId; 'other root'");

            Assert.Equal(
                [app.Id.ToString(), "by folder", app.Id.ToString(), "by root", "neighbour", "other root"],
                Lines(result.Output));
        }
        finally
        {
            app.Kill();
            app.WaitForExit();
        }
    }

    [Fact]
    public void ACleanThatStopsHalfWay_LeavesTheMarker_AlsoThroughAShortPath()
    {
        // C:\Users\LITTLE~1\... is what %TEMP% looks like for a user name with a space in it. The
        // folder listing writes that name out long, so a marker looked for by its full path is never
        // found, and goes with everything else.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "a long folder name", "StoryForgeX-1.2.2.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '1.2.2.9'; " +
            $"Set-Content '{folder}\\free.dll' x; Set-Content '{folder}\\zz-held.dll' x; " +
            $"$short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder('{folder}').ShortPath; " +
            $"if ($short -ieq '{folder}') {{ 'no short names here'; return }}; " +
            $"$held = [IO.File]::Open('{folder}\\zz-held.dll', 'Open', 'Read', 'None'); " +
            $"try {{ Remove-InstallFolder $short; 'cleaned' }} catch {{ 'stopped' }} finally {{ $held.Dispose() }}");

        if (result.Output.Trim() == "no short names here")
        {
            return; // The volume keeps no 8.3 names, so there is no short spelling to pass.
        }

        Assert.Equal("stopped", result.Output.Trim());
        Assert.True(File.Exists(Path.Combine(folder, "storyforgex-install.json")));
        Assert.False(File.Exists(Path.Combine(folder, "free.dll")));
    }

    [Fact]
    public void ACleanThatStopsHalfWay_LeavesTheMarker()
    {
        // The marker is what lets the next run clean the folder. A plain recursive delete can take it
        // before it stops at a file somebody holds open - leaving a folder every later run refuses to
        // publish into and refuses to clean.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "StoryForgeX-1.2.2.9");

        var result = Pwsh.RunCommon(
            $"Write-InstallMarker '{folder}' '1.2.2.9'; " +
            $"Set-Content '{folder}\\free.dll' x; Set-Content '{folder}\\zz-held.dll' x; " +
            $"$held = [IO.File]::Open('{folder}\\zz-held.dll', 'Open', 'Read', 'None'); " +
            $"try {{ Remove-InstallFolder '{folder}'; 'cleaned' }} catch {{ 'stopped' }} finally {{ $held.Dispose() }}");

        Assert.Equal("stopped", result.Output.Trim());
        Assert.True(File.Exists(Path.Combine(folder, "storyforgex-install.json")));
        Assert.False(File.Exists(Path.Combine(folder, "free.dll")));
    }

    [Fact]
    public void TheStableBuild_HasNoRootOfItsOwnToPointElsewhere()
    {
        // publish-bump.ps1 treated an output root outside E:\StableVersion as a one-off build. Here a
        // different -InstallRoot would still bump, commit and push main and repoint the real Start
        // Menu shortcut - a trap for that habit. -InstallDir is the one way to build somewhere else.
        var buildScript = Path.Combine(Pwsh.ScriptsFolder, "build.ps1");

        var result = Pwsh.Run($"(Get-Command '{buildScript}').Parameters.Keys -join ','");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("InstallDir", result.Output.Split(','));
        Assert.DoesNotContain("InstallRoot", result.Output.Trim().Split(','));
    }

    [Fact]
    public void AStableBuild_BumpsPublishesAndMarksTheFolderFinished()
    {
        using var repo = new FakeRepo("1.2.2.8");

        var result = repo.Build();

        Assert.True(result.ExitCode == 0, result.Output);
        var folder = repo.VersionFolder("1.2.2.9");
        Assert.Equal("1.2.2.9", repo.Version);
        Assert.True(FakeRepo.IsMarkedComplete(folder));
        Assert.Equal(folder, repo.CurrentLinkTarget);
        Assert.Contains("shortcut " + repo.CurrentLink, repo.Calls);
        Assert.Contains("git push --quiet origin main", repo.Calls);
        Assert.Equal(
            ["dotnet build StoryForgeX.slnx", "dotnet test StoryForgeX.slnx", "dotnet publish StoryForge.App.csproj"],
            repo.Calls.Where(call => call.StartsWith("dotnet ")));
    }

    [Fact]
    public void AReRunAfterALostBump_LeavesTheInstalledVersionAlone()
    {
        // The whole chain of the bug: the build is published and made current, the push is rejected,
        // and a reset to origin takes the bump commit away. The next run computes the same number.
        using var repo = new FakeRepo("1.2.2.8");
        var folder = repo.VersionFolder("1.2.2.9");
        var rejected = repo.Build(before: "$env:FAKE_PUSH_FAILS = '1'");
        Assert.Contains("the push was rejected", rejected.Output);
        repo.Version = "1.2.2.8";

        var result = repo.Build();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1.2.2.9 is already published", result.Output);
        Assert.True(File.Exists(Path.Combine(folder, "StoryForge.App.exe")));
        Assert.Equal("1.2.2.8", repo.Version);
    }

    [Fact]
    public void AFolderThatNeverBecameCurrent_IsRebuiltByTheNextRun()
    {
        // Published, but the link could not be made: the folder was never used and the version never
        // bumped. Marked finished at that point, every later run would refuse it for good.
        using var repo = new FakeRepo("1.2.2.8");
        var folder = repo.VersionFolder("1.2.2.9");
        var failed = repo.Build(before: "$env:FAKE_LINK_FAILS = '1'");
        Assert.Contains("the junction could not be made", failed.Output);
        Assert.False(FakeRepo.IsMarkedComplete(folder));
        Assert.Equal("1.2.2.8", repo.Version);

        var result = repo.Build();

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(FakeRepo.IsMarkedComplete(folder));
        Assert.Equal("1.2.2.9", repo.Version);
    }

    [Fact]
    public void ABuildSomebodyStartedByHandBeforeItWasCurrent_IsNotCleanedUnderThem()
    {
        // The folder is not finished, so the guard would let the next run clean it - and the clean
        // would delete what it can and stop at the locked exe.
        using var repo = new FakeRepo("1.2.2.8");
        var folder = repo.VersionFolder("1.2.2.9");
        var exe = Path.Combine(folder, "StoryForge.App.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "storyforgex-install.json"), """{ "app": "StoryForgeX", "version": "1.2.2.9", "complete": false }""");
        File.WriteAllText(exe, "x");

        var result = repo.Build(before: $"$env:FAKE_RUNNING_FROM = '{exe}'");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"StoryForgeX is running from {folder}", result.Output);
        Assert.True(File.Exists(exe));
        Assert.DoesNotContain(repo.Calls, call => call.StartsWith("dotnet "));
    }

    [Fact]
    public void AOneOffBuild_IsStampedAndLeavesTheRepoAlone()
    {
        using var repo = new FakeRepo("1.2.2.8");
        var folder = Path.Combine(repo.Outside, "StoryForgeX-test");

        var result = repo.Build($"-InstallDir '{folder}'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("\"version\": \"1.2.2.8-oneoff.gabc1234\"", File.ReadAllText(Path.Combine(folder, "storyforgex-install.json")));
        Assert.True(FakeRepo.IsMarkedComplete(folder));
        Assert.Equal("1.2.2.8", repo.Version);
        Assert.DoesNotContain(repo.Calls, call => call.StartsWith("git commit") || call.StartsWith("git push") || call.StartsWith("shortcut"));
        Assert.Null(repo.CurrentLinkTarget);
    }

    [Fact]
    public void AOneOffBuild_ThroughAnAliasOfTheStableRoot_IsRefused()
    {
        // The folder does not exist yet, so there is no marker to read: only the real path can tell
        // that this one-off would land in the stable root, under the next stable version's name.
        using var repo = new FakeRepo("1.2.2.8");
        var alias = Path.Combine(repo.Outside, "alias");

        var result = repo.Build(
            $"-InstallDir '{Path.Combine(alias, "StoryForgeX-1.2.2.9")}'",
            before: $"New-Item -ItemType Junction -Path '{alias}' -Target '{repo.Root}' | Out-Null");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must be outside the stable root", result.Output);
        Assert.False(Directory.Exists(repo.VersionFolder("1.2.2.9")));
    }

    [Fact]
    public void ABuildWhoseCleanStopsHalfWay_LeavesAFolderTheNextRunCanClean()
    {
        // build.ps1 itself has to delete the marker last, not only the helper it calls: one file in
        // the folder is held open, the clean stops there, and the next run must still own the folder.
        using var repo = new FakeRepo("1.2.2.8");
        var folder = Path.Combine(repo.Outside, "StoryForgeX-test");
        Assert.Equal(0, repo.Build($"-InstallDir '{folder}'").ExitCode);
        var held = Path.Combine(folder, "zz-held.dll");
        File.WriteAllText(held, "x");

        PwshResult stopped;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            stopped = repo.Build($"-InstallDir '{folder}' -Clean");
        }

        Assert.Equal(1, stopped.ExitCode);
        Assert.Contains("Could not clean", stopped.Output);
        Assert.True(File.Exists(Path.Combine(folder, "storyforgex-install.json")));
        Assert.False(File.Exists(Path.Combine(folder, "StoryForge.App.exe")));

        var result = repo.Build($"-InstallDir '{folder}' -Clean");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(held));
    }

    [Fact]
    public void AOneOffBuild_NeverCleansAStableFolder()
    {
        using var repo = new FakeRepo("1.2.2.8");
        var folder = Directory.CreateDirectory(Path.Combine(repo.Outside, "looks-like-a-test-folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "storyforgex-install.json"), """{ "app": "StoryForgeX", "version": "1.2.2.8", "complete": true }""");
        File.WriteAllText(Path.Combine(folder, "StoryForge.App.exe"), "x");

        var result = repo.Build($"-InstallDir '{folder}' -Clean");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not a one-off StoryForgeX build", result.Output);
        Assert.True(File.Exists(Path.Combine(folder, "StoryForge.App.exe")));
    }

    [Fact]
    public void AGoodBuild_DoesNotWaitForAKey_UnlessExplorerStartedIt()
    {
        // PowerShell, Git Bash, a VS Code task and a CI step all run a .cmd as cmd /c "<path>",
        // exactly as a double click does. Their window stays open, or nobody is there - and a caller
        // that leaves stdin open would wait for ever. This test process is such a caller. -? is a
        // quick success.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(PauseLine, result.Output);
    }

    [Fact]
    public void AFailedBuild_WaitsForAKey()
    {
        // -Part takes four values; anything else stops PowerShell before build.ps1 runs a line.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -Part nonsense\"");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(PauseLine, result.Output);
    }

    [Theory]
    [InlineData("always", "-?", true)]
    [InlineData("never", "-Part nonsense", false)]
    [InlineData("never ", "-Part nonsense", false)]
    [InlineData(" Always", "-?", true)]
    public void TheWait_CanBeForcedOrSwitchedOff(string setting, string arguments, bool waits)
    {
        // "always" is what a double click amounts to; "never" is for a caller that must not hang.
        // The space is what "set STORYFORGEX_BUILD_PAUSE=never && build.cmd" stores at the end.
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" {arguments}\"", ("STORYFORGEX_BUILD_PAUSE", setting));

        Assert.Equal(waits, result.Output.Contains(PauseLine));
    }

    [Fact]
    public void ASettingThatIsNeitherWord_IsIgnoredOutLoud()
    {
        var result = Cmd.Run($"/c \"\"{BuildCmd}\" -Part nonsense\"", ("STORYFORGEX_BUILD_PAUSE", "off"));

        Assert.Contains("neither never nor always", result.Output);
        Assert.Contains(PauseLine, result.Output);
    }

    [Theory]
    [InlineData("/c", true, 0)]
    [InlineData("/c", false, 1)]
    [InlineData("/k", true, 1)]
    public void StartedByExplorer_MeansACmdSlashC_WhoseParentIsExplorer(string cmdSwitch, bool parentIsTheStarter, int expected)
    {
        // A test cannot have Explorer as its parent, so the helper is told to look for this test
        // process instead. The chain is a double click's: helper, cmd /c "<file>", starter. cmd /k
        // is an open Command Prompt, which runs a typed .cmd inside itself: Explorer's child too,
        // but no double click.
        using var temp = new TempFolder();
        using var self = Process.GetCurrentProcess();
        var starter = parentIsTheStarter ? Path.GetFileName(self.MainModule!.FileName) : "somebody-else.exe";
        var wrapper = Path.Combine(temp.Path, "wrapper.cmd");
        File.WriteAllText(
            wrapper,
            $"@pwsh -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(Pwsh.ScriptsFolder, "_started-by-explorer.ps1")}\" -StarterName \"{starter}\"\r\n" +
            "@echo result=%ERRORLEVEL%\r\n");

        var result = Cmd.Run($"{cmdSwitch} \"\"{wrapper}\"\"");

        Assert.Contains($"result={expected}", result.Output);
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildCmd => Path.Combine(Pwsh.ScriptsFolder, "build.cmd");

    /// <summary>
    /// A throwaway copy of the scripts in a repo-shaped folder, with a stable root of its own. The
    /// copy of _common.ps1 ends with fakes for everything that reaches outside that folder: git, the
    /// SDK, the Start Menu shortcut and the list of running apps. build.ps1 is the real file.
    /// </summary>
    internal sealed class FakeRepo : IDisposable
    {
        private readonly TempFolder _temp = new();

        public FakeRepo(string version)
        {
            Root = Directory.CreateDirectory(Path.Combine(_temp.Path, "stable")).FullName;
            Outside = Directory.CreateDirectory(Path.Combine(_temp.Path, "builds")).FullName;
            var scripts = Directory.CreateDirectory(Path.Combine(Repo, "scripts")).FullName;
            File.Copy(Path.Combine(Pwsh.ScriptsFolder, "build.ps1"), Path.Combine(scripts, "build.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "_common.ps1"),
                File.ReadAllText(Path.Combine(Pwsh.ScriptsFolder, "_common.ps1")) + Fakes);
            var project = Directory.CreateDirectory(Path.Combine(Repo, "StoryForge.App")).FullName;
            File.WriteAllText(Path.Combine(project, "StoryForge.App.csproj"), "<Project />");
            Version = version;
        }

        private string Repo => Path.Combine(_temp.Path, "repo");

        private string Log => Path.Combine(_temp.Path, "calls.log");

        private string Props => Path.Combine(Repo, "Directory.Build.props");

        /// <summary>The stable root: what E:\StableVersion is on the real machine.</summary>
        public string Root { get; }

        /// <summary>A folder outside both the repo and the stable root, for one-off builds.</summary>
        public string Outside { get; }

        public string CurrentLink => Path.Combine(Root, "StoryForgeX");

        public string? CurrentLinkTarget => new DirectoryInfo(CurrentLink).LinkTarget;

        public string[] Calls => File.Exists(Log) ? File.ReadAllLines(Log) : [];

        public string Version
        {
            get => Regex.Match(File.ReadAllText(Props), "<Version>([^<]+)</Version>").Groups[1].Value;
            set => File.WriteAllText(Props, $"<Project>\r\n  <PropertyGroup>\r\n    <Version>{value}</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n");
        }

        public string VersionFolder(string version) => Path.Combine(Root, $"StoryForgeX-{version}");

        public static bool IsMarkedComplete(string folder) =>
            Regex.IsMatch(File.ReadAllText(Path.Combine(folder, "storyforgex-install.json")), "\"complete\":\\s*true");

        /// <summary>Runs the real build.ps1 against this repo. <paramref name="before"/> sets the fakes' switches.</summary>
        public PwshResult Build(string arguments = "", string before = "") =>
            Pwsh.Run($"{before}\n& '{Path.Combine(Repo, "scripts", "build.ps1")}' {arguments}");

        public void Dispose() => _temp.Dispose();

        private string Fakes => $$"""


            # --- test fakes: nothing below reaches outside the test's own folder ---
            $script:DefaultInstallRoot = '{{Root}}'
            function Write-FakeCall { param([string]$Line) Add-Content -LiteralPath '{{Log}}' -Value $Line }
            function git {
                $global:LASTEXITCODE = 0
                $rest = @($args | Select-Object -Skip 2)
                Write-FakeCall "git $($rest -join ' ')"
                switch ($rest[0]) {
                    'rev-parse' { if ($rest -contains '--short') { 'abc1234' } else { 'main' } }
                    'rev-list'  { "0`t0" }
                    'push'      { if ($env:FAKE_PUSH_FAILS) { $global:LASTEXITCODE = 1 } }
                }
            }
            function dotnet {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq '--version') { return '10.0.0-fake' }
                Write-FakeCall "dotnet $($args[0]) $(Split-Path -Leaf $args[1])"
                if ($args[0] -ne 'publish') { return }
                $out = $args[[array]::IndexOf($args, '--output') + 1]
                New-Item -ItemType File -Path (Join-Path $out $AppExeName) -Force | Out-Null
            }
            function Write-StartMenuShortcut { param([string]$InstallDir) Write-FakeCall "shortcut $InstallDir"; return 'fake.lnk' }
            function Get-AppProcess {
                param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot)
                if (-not $env:FAKE_RUNNING_FROM) { return @() }
                if (-not $AnyVersion -and $env:FAKE_RUNNING_FROM -ine (Join-Path (Resolve-FullPath $InstallDir) $AppExeName)) { return @() }
                return @([pscustomobject]@{ ProcessId = 4242; ExecutablePath = $env:FAKE_RUNNING_FROM })
            }
            if ($env:FAKE_LINK_FAILS) { function Set-CurrentLink { throw 'the junction could not be made' } }

            """;
    }

    internal static class Cmd
    {
        public static PwshResult Run(string arguments, params (string Name, string Value)[] environment) =>
            Shell.Run("cmd.exe", arguments, stdin: "", environment);
    }

    internal sealed record PwshResult(int ExitCode, string Output);

    internal static class Pwsh
    {
        public static string ScriptsFolder { get; } = Path.Combine(FindRepoRoot(), "scripts");

        /// <summary>Runs <paramref name="command"/> after dot-sourcing _common.ps1, as build.ps1 does.</summary>
        public static PwshResult RunCommon(string command) =>
            Run($". '{Path.Combine(ScriptsFolder, "_common.ps1")}'\n{command}");

        public static PwshResult Run(string script) =>
            Shell.Run("pwsh", "-NoProfile -NonInteractive -Command -", script + "\nexit $LASTEXITCODE\n");

        /// <summary>
        /// The test output can be anywhere - -p:BaseOutputPath is how a build dodges a DLL the running
        /// app holds - so when it is not under the repo, this source file's own path is asked instead.
        /// </summary>
        private static string FindRepoRoot([CallerFilePath] string thisFile = "")
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
            {
                for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "StoryForgeX.slnx")))
                    {
                        return directory.FullName;
                    }
                }
            }

            throw new InvalidOperationException("StoryForgeX.slnx not found above the test output or above this source file.");
        }
    }

    internal static class Shell
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        public static PwshResult Run(string fileName, string arguments, string stdin, params (string Name, string Value)[] environment)
        {
            var start = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            // A script that waits - a pause reading a console, a real git asking for a login - would
            // otherwise hang the whole test run with nothing to say which test it was.
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10));
                var soFar = stdout.IsCompleted && stderr.IsCompleted ? stdout.Result + stderr.Result : "(not readable)";
                throw new TimeoutException($"{fileName} did not finish within {Timeout.TotalSeconds:0} s and was killed. Output so far:\n{soFar}");
            }

            return new PwshResult(process.ExitCode, stdout.Result + stderr.Result);
        }
    }

    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StoryForgeX.BuildScriptTests-" + Guid.NewGuid())).FullName;

        public void Dispose()
        {
            try
            {
                RemoveLinks(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: a lingering handle must not fail the suite.
            }
        }

        /// <summary>A recursive delete refuses a junction, so each one is taken out first - the link, never its target.</summary>
        private static void RemoveLinks(DirectoryInfo folder)
        {
            foreach (var child in folder.EnumerateDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    child.Delete();
                }
                else
                {
                    RemoveLinks(child);
                }
            }
        }
    }
}
