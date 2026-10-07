<#
.SYNOPSIS
    Builds and tests StoryForgeX in Release and installs it as the next stable version.

.DESCRIPTION
    Bumps the version, builds, runs the test suite, publishes the app into a folder named after the
    version under E:\StableVersion (StoryForgeX-1.2.2.9), points E:\StableVersion\StoryForgeX at it - the
    fixed path the Start Menu entry and any taskbar pin open - and then commits the bump and pushes
    main. Older versions stay beside the new one; delete them by hand when you like.

    Tests come before the publish on purpose: a build you are about to use is the worst place to
    discover a red suite. A failed build or test run leaves the repo and the stable root as they were.

    A running StoryForgeX is left alone - it may be in the middle of a run - and keeps running the
    version it was started from. The script says so; restart it to get the new one.

    That only happens from a clean main that is level with origin. On any other branch the script
    stops before building anything; give it -InstallDir for a build that goes somewhere else.

    The published folder is framework-dependent - it needs the .NET 10 desktop runtime - and holds
    the Windows x64 natives only.

.PARAMETER Part
    Which part of the version to increase: major, minor, patch or build. Defaults to build, the
    everyday "another build of the same release" case: 1.2.2 -> 1.2.2.1 -> 1.2.2.2. A release
    bump zeroes everything to its right and drops the fourth part: 1.2.2.8 -Part minor -> 1.3.0.

.PARAMETER InstallDir
    A folder of your own for the build instead of a versioned one. Nothing is bumped, committed
    or pushed, no Start Menu shortcut is written, and any branch is fine - this is the way to try a
    feature branch's build. The build is stamped <current version>-oneoff.g<commit> so it cannot be
    mistaken for a stable one, with .dirty on the end when it holds uncommitted changes. Must be outside the repo and outside the stable root, and must be
    empty or a folder this script published into before - a folder holding anything else is
    refused untouched.

.PARAMETER Clean
    With -InstallDir: wipe that folder before publishing. Only ever a folder carrying our install
    marker. A versioned folder always starts empty, so the switch has nothing to do there.

.PARAMETER SkipTests
    Skip the test run. For when you have just run the suite.

.PARAMETER NoShortcut
    Leave the Start Menu shortcut as it is. A one-off -InstallDir build never writes it.

.EXAMPLE
    .\scripts\build.ps1
    The next stable version: bump, build, test, publish, commit, push.

.EXAMPLE
    .\scripts\build.ps1 -Part minor
    1.2.2.8 -> 1.3.0, for an actual release rather than another build of one.

.EXAMPLE
    .\scripts\build.ps1 -InstallDir E:\Builds\StoryForgeX-test -SkipTests
    Try this branch's build without touching the stable versions, the Start Menu or the repo.
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch', 'build')]
    [string]$Part = 'build',
    # Never empty: "-InstallDir $env:SOME_DIR" with the variable unset would otherwise read as no
    # folder given - a full stable release instead of a one-off.
    [ValidateNotNullOrEmpty()]
    [string]$InstallDir,
    [switch]$Clean,
    [switch]$SkipTests,
    [switch]$NoShortcut
)

. (Join-Path $PSScriptRoot '_common.ps1')

# Fixed, not a parameter. A stable build bumps, pushes and repoints the Start Menu wherever it goes,
# so a root of your own would be a full release somewhere unexpected; -InstallDir is the way to build
# somewhere else.
$InstallRoot = Resolve-FullPath $DefaultInstallRoot

# Two ways in: no -InstallDir means the next stable version, with everything that entails; an
# -InstallDir is a one-off build that leaves the repo and the Start Menu alone.
$stable = -not $InstallDir
if (-not $stable) { $InstallDir = Resolve-FullPath $InstallDir }

Write-Host ""
Write-Host "  StoryForgeX - Release build" -ForegroundColor White
if ($stable) { Write-Note "root     $InstallRoot  (next stable version)" }
else         { Write-Note "install  $InstallDir  (one-off build, no version bump)" }

# --- preflight ---------------------------------------------------------------------------
Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "The .NET SDK is not on your PATH." @("Install the .NET 10 SDK from https://dotnet.microsoft.com/download")
}
Write-Ok ".NET SDK $(dotnet --version)"

if (-not (Test-Path -LiteralPath $AppProject)) {
    Fail "Cannot find $AppProject." @("Run this script from a clone of the StoryForgeX repo.")
}

$current = Get-PropsVersion
$version = ''
if ($stable) {
    # Branch, clean tree and origin are checked before a single file is built, so a wrong branch
    # costs seconds, not a build and a test run.
    Assert-ReleaseReady
    $version = Get-NextVersion -Current $current -Part $Part
    $InstallDir = Get-VersionedInstallDir -InstallRoot $InstallRoot -Version $version
    Assert-StableTargetFree -InstallDir $InstallDir -Version $version -Current $current
    Write-Ok "version $current -> $version"
    Write-Note "install  $InstallDir"
    Write-Note "Directory.Build.props is bumped, committed and pushed only if the publish succeeds"
    if ($Clean) { Write-Warn "-Clean has nothing to do here: a versioned folder always starts empty" }
}
else {
    # FileVersion stays numeric, as Windows requires; only the product version carries the stamp.
    $version = Get-OneOffVersion -Current $current -Commit (Get-HeadCommit) -Dirty:(Test-UncommittedChanges)
    Write-Ok "version $version"
    if ($NoShortcut) { Write-Note "-NoShortcut is implied: a one-off build never writes the Start Menu shortcut" }
    if ($PSBoundParameters.ContainsKey('Part')) { Write-Warn "-Part has nothing to do here: a one-off build is stamped with the current version, never bumped" }
}

# An install folder inside the repo gets swept up by the next build, and each build then copies the
# previous build's output into itself.
if (Test-PathUnder $InstallDir $RepoRoot) {
    Fail "-InstallDir must be outside the repo ($RepoRoot)." @(
        "Publishing into the repo makes every later build copy the last build into itself.",
        "Leave -InstallDir unset to build the next stable version under $InstallRoot."
    )
}

# The stable root is for versioned builds from main only. A hand-picked folder in there would be
# a "stable version" that skipped every check above.
if (-not $stable -and (Test-PathUnder $InstallDir $InstallRoot -OrEqual)) {
    Fail "-InstallDir must be outside the stable root ($InstallRoot)." @(
        "That folder holds the stable versions, which only build.ps1 without -InstallDir makes, from main.",
        "Put a one-off build somewhere else, for example:",
        "  .\scripts\build.ps1 -InstallDir 'E:\Builds\StoryForgeX-test'"
    )
}

# The install root is shared with other releases (E:\StableVersion holds CodeSwitchX, ContentAutomatorX,
# RawCutX and friends). A fresh versioned build deletes its folder first, so we only ever work in a folder
# that is empty or already carries our install marker - and a one-off only in a one-off's folder,
# a second line behind the path check above.
if ((Test-DirectoryHasContent $InstallDir) -and -not (Test-OurInstall $InstallDir -OneOff:(-not $stable))) {
    $what = if ($stable) { 'a StoryForgeX install made by this script' } else { 'a one-off StoryForgeX build made by this script' }
    Fail "$InstallDir already holds files that are not $what." @(
        "Not one of them was touched.",
        "Move that folder out of the way (delete it by hand if it is an old test build), or give the build a folder of its own:",
        "  .\scripts\build.ps1 -InstallDir 'E:\Builds\StoryForgeX-test'"
    )
}

# A one-off build overwrites its folder in place, and a running StoryForgeX holds its files. It is not
# closed for you: it may be in the middle of a run. A stable build publishes into a new
# folder nothing has locked, so it never needs this.
if (-not $stable) {
    $locking = @(Get-AppProcess $InstallDir)
    if ($locking.Count -gt 0) {
        Fail "StoryForgeX is running from $InstallDir (PID $($locking[0].ProcessId))." @(
            "Close it, then run the build again. It is not closed for you, in case it is busy."
        )
    }
}

$buildFailedHints = @("Scroll up for the first error - warnings count as errors here (Directory.Build.props).")
if ($stable) { $buildFailedHints += "Directory.Build.props is untouched, still $current." }

# --- build and test ----------------------------------------------------------------------
# PowerShell unwraps a one-element array from an `if`, and splatting a plain string with @ hands
# MSBuild the characters one by one - so the array is built explicitly rather than cast.
$versionArgs = @("-p:Version=$version")

Write-Step "Building (Release)"
Write-Note "warnings are errors in this repo, so a warning will stop the build"
dotnet build $Solution --configuration Release --nologo -v minimal @versionArgs
if ($LASTEXITCODE -ne 0) {
    Fail "The build failed (exit code $LASTEXITCODE)." $buildFailedHints
}

if ($SkipTests) {
    Write-Step "Tests SKIPPED (-SkipTests)"
}
else {
    Write-Step "Running tests"
    dotnet test $Solution --configuration Release --no-build --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        Fail "The test run failed (exit code $LASTEXITCODE)." @(
            @("Nothing was published.") + @($buildFailedHints | Select-Object -Skip 1)
        )
    }
}

# --- clean -------------------------------------------------------------------------------
# A versioned folder always starts empty. It can only exist from an earlier run of this exact
# version that failed after claiming it - the number was not written back, so it comes round again.
if (($stable -or $Clean) -and (Test-DirectoryHasContent $InstallDir)) {
    Write-Step "Cleaning $InstallDir"

    # Belt and braces: the guard above already rejected a folder that is not ours, but a recursive
    # delete checks for itself too.
    if (-not (Test-OurInstall $InstallDir -OneOff:(-not $stable))) {
        Fail "Refusing to clean $InstallDir - it carries no StoryForgeX install marker." @(
            "Only a folder this script published into is ever deleted."
        )
    }

    try { Remove-InstallFolder $InstallDir }
    catch {
        Fail "Could not clean $InstallDir - $($_.Exception.Message)" @(
            "Something is probably still holding a file there. Close it and try again."
        )
    }
    Write-Ok "removed"
}

# --- claim the folder ----------------------------------------------------------------------
# Before the publish, not after. A publish interrupted part-way (Ctrl+C, a warning-as-error, a
# virus scanner holding a file) would otherwise leave a folder full of files and no marker, which
# every later run refuses to publish into *and* refuses to clean. The checks above established
# the folder is empty or ours, so claiming it here is safe. The marker says "complete": false until
# a one-off's publish has succeeded or a stable build has been made current. A complete stable
# folder is never cleaned again (Assert-StableTargetFree), so "complete" must not be said of a
# build that never became current - the next run has to be able to rebuild that one.
Write-InstallMarker -InstallDir $InstallDir -Version $version

# --- publish -----------------------------------------------------------------------------
Write-Step "Publishing"
# Framework-dependent, for this machine's kind of Windows: without a runtime the SDK also copies the
# SQLite natives for Linux and macOS, which nothing here ever loads.
dotnet publish $AppProject --configuration Release --runtime win-x64 --self-contained false --output $InstallDir --nologo -v minimal @versionArgs
if ($LASTEXITCODE -ne 0) {
    Fail "The publish failed (exit code $LASTEXITCODE)." $buildFailedHints
}

foreach ($required in $RequiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir $required))) {
        Fail "The publish reported success but $required is missing from $InstallDir." $buildFailedHints
    }
}

$files = @(Get-ChildItem -LiteralPath $InstallDir -Recurse -File)
Write-Note ("{0:N1} MB in {1:N0} files" -f (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count)

if (-not $stable) { Write-InstallMarker -InstallDir $InstallDir -Version $version -Complete }
Write-Ok "published to $InstallDir"

# --- make it the current version ------------------------------------------------------------
# The fixed path E:\StableVersion\StoryForgeX is what the Start Menu entry and any taskbar pin open.
# Pointing it at the new folder is what makes this build "current" without every pin on the machine
# having to change.
$shortcutDir = $InstallDir
$running = @()
if ($stable) {
    Write-Step "Making $version the current version"
    if (Set-CurrentLink -InstallRoot $InstallRoot -Target $InstallDir) {
        $shortcutDir = Get-CurrentLinkPath $InstallRoot
        Write-Ok "$shortcutDir -> $InstallDir"
    }
    else {
        Write-Warn "no current link; the shortcut will point at $InstallDir directly, and a taskbar pin made earlier still opens the old build"
    }
    # Only now, with the folder current. Had the link thrown, the folder would be finished on
    # paper yet never used, and every later run would refuse to rebuild it.
    Write-InstallMarker -InstallDir $InstallDir -Version $version -Complete

    if ($NoShortcut) {
        Write-Ok "Start Menu shortcut left as it is, as asked"
        if ($shortcutDir -eq $InstallDir) {
            Write-Warn "the existing Start Menu shortcut still opens the previous build"
        }
    }
    else {
        # The folder is current and finished by now, so a failure here must not end the run: the bump
        # would never be recorded, and the next run would refuse this number as already published.
        try {
            $linkPath = Write-StartMenuShortcut $shortcutDir
            Write-Ok "Start Menu shortcut at $linkPath"
            Write-Note "right-click it in the Start Menu and choose 'Pin to taskbar'"
        }
        catch {
            Write-Warn "the Start Menu shortcut could not be written - $($_.Exception.Message)"
            Write-Warn "the build is fine; start it from $(Join-Path $shortcutDir $AppExeName)"
        }
    }

    $running = @(Get-AppProcess -AnyVersion -InstallRoot $InstallRoot)
}

# --- record the version -------------------------------------------------------------------
if ($stable) {
    Write-Step "Recording version $version"
    Set-PropsVersion $version
    Write-Ok "Directory.Build.props bumped to $version"
    Push-VersionBump $version
    Write-Ok "committed and pushed to origin/$ReleaseBranch"
}

# --- done ----------------------------------------------------------------------------------
$exePath = Join-Path $shortcutDir $AppExeName
Write-Host ""
if ($stable) { Write-Host "  Build complete - StoryForgeX $version" -ForegroundColor Green }
else         { Write-Host "  Build complete - StoryForgeX $version (one-off)" -ForegroundColor Green }
foreach ($proc in $running) {
    # Every version opens the same database in %LOCALAPPDATA%\StoryForgeX, and a new one may migrate
    # it on start - under the old one, which keeps working on the schema it knows.
    Write-Warn "StoryForgeX is still running from $($proc.ExecutablePath) (PID $($proc.ProcessId)) - the old version."
    Write-Warn "Close it before you start $version - both use the same database."
}
if ($stable -and -not $NoShortcut) { Write-Host "  Start it from the Start Menu (StoryForge X), or: " -NoNewline }
else                               { Write-Host "  Start it: " -NoNewline }
Write-Host $exePath -ForegroundColor White
if ($stable) {
    Write-Note "tag it if this is a release:  git tag v$version; git push origin v$version"
}
Write-Host ""
