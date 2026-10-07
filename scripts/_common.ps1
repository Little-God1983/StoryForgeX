# Shared settings and helpers for the StoryForgeX build scripts.
# Dot-sourced by build.ps1 - not meant to be run on its own.

$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ turns a non-zero native exit code into a thrown exception. We check
# $LASTEXITCODE ourselves so the failure prints as a readable message, not a stack trace.
if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:RepoRoot   = Split-Path -Parent $PSScriptRoot
$script:Solution   = Join-Path $RepoRoot 'StoryForgeX.slnx'
$script:AppProject = Join-Path $RepoRoot 'StoryForge.App\StoryForge.App.csproj'

# Every stable build gets a folder of its own under this root, named after the app and the version it
# was built at: E:\StableVersion\StoryForgeX-1.2.2.9. Older versions stay beside it. The version is the
# <Version> element in Directory.Build.props - the one place it lives - and build.ps1 moves it on,
# commits that one file and pushes main, so the number in the repo always names a build that exists on
# disk. E:\StableVersion\StoryForgeX is a junction to the newest one.
#
# The app keeps its database, settings and reference files in %LOCALAPPDATA%\StoryForgeX whichever
# folder it runs from, so an install folder holds program files only.
$script:AppName            = 'StoryForgeX'
$script:DefaultInstallRoot = 'E:\StableVersion'
$script:ReleaseBranch      = 'main'
$script:PropsPath          = Join-Path $RepoRoot 'Directory.Build.props'
$script:AppExeName         = 'StoryForge.App.exe'
$script:ShortcutName       = 'StoryForge X.lnk'

# build.ps1 drops this in the install folder. Nothing is ever deleted from a folder that does
# not carry it, so a mistyped -InstallDir cannot take out a neighbouring release.
$script:InstallMarkerName = 'storyforgex-install.json'

function Write-Step { param([string]$Message) Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Ok   { param([string]$Message) Write-Host "    [ok] $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    [!]  $Message" -ForegroundColor Yellow }

function Fail {
    param([string]$Message, [string[]]$Hints)
    Write-Host ""
    Write-Host "    [x] $Message" -ForegroundColor Red
    foreach ($hint in $Hints) { Write-Host "        $hint" -ForegroundColor Yellow }
    Write-Host ""
    exit 1
}

# Absolute path, with no requirement that it already exists.
function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $Path))
}

# The compiled helper behind Resolve-RealPath and Get-FolderId. A type added with Add-Type stays in
# the PowerShell window until it closes, so the name is this repo's own and carries a number: change
# the number whenever the C# changes, or a window that ran an older copy keeps using that one. The
# C# is kept to what Windows PowerShell 5.1 compiles.
function Get-PathInfoType {
    $name = 'StoryForgeX.BuildScripts.PathInfoV2'
    if (-not ($name -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StoryForgeX.BuildScripts
{
    public static class PathInfoV2
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public long Created;
            public long Accessed;
            public long Written;
            public uint VolumeSerial;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        // No access asked for, every kind of sharing allowed, and BACKUP_SEMANTICS so a folder opens.
        private static SafeFileHandle Open(string path)
        {
            return CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        }

        public static string RealPath(string path)
        {
            using (SafeFileHandle handle = Open(path))
            {
                if (handle.IsInvalid) { return null; }
                StringBuilder buffer = new StringBuilder(4096);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) { return null; }
                string real = buffer.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) { return @"\\" + real.Substring(8); }
                if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) { return real.Substring(4); }
                return real;
            }
        }

        // Which file or folder it is, however the path is spelled: the serial number of the volume
        // and the number of the file on that volume.
        public static string Id(string path)
        {
            using (SafeFileHandle handle = Open(path))
            {
                if (handle.IsInvalid) { return null; }
                FileInformation information;
                if (!GetFileInformationByHandle(handle, out information)) { return null; }
                return information.VolumeSerial.ToString("x8") + ":" + information.IndexHigh.ToString("x8") + information.IndexLow.ToString("x8");
            }
        }
    }
}
'@
    }
    return ($name -as [type])
}

# The path with every alias taken out of it: an 8.3 name (E:\STABLE~1), a junction or symlink, a
# subst drive. Windows only resolves those for something that exists, so the nearest existing
# ancestor is asked and whatever is not there yet is put back on the end. Falls back to the plain
# full path when Windows cannot say.
function Resolve-RealPath {
    param([string]$Path)
    $full = Resolve-FullPath $Path
    $existing = $full
    $missing  = @()
    while ($existing -and -not (Test-Path -LiteralPath $existing)) {
        $missing  = @([System.IO.Path]::GetFileName($existing.TrimEnd('\'))) + $missing
        $existing = [System.IO.Path]::GetDirectoryName($existing.TrimEnd('\'))
    }
    if (-not $existing) { return $full }
    $real = (Get-PathInfoType)::RealPath($existing)
    if (-not $real) { return $full }
    foreach ($name in $missing) { $real = Join-Path $real $name }
    return $real
}

# Which file or folder a path leads to, as an ID that is the same through every spelling of it, or
# $null for something that does not exist. A real path cannot do this for a share that leads back
# to this PC: \\localhost\E$\StableVersion stays a share path, and only the ID says it is
# E:\StableVersion.
function Get-FolderId {
    param([string]$Path)
    return (Get-PathInfoType)::Id((Resolve-FullPath $Path))
}

# True when two paths lead to the same file or folder, whether or not it exists yet.
function Test-SamePath {
    param([string]$Path, [string]$Other)
    if ((Resolve-RealPath $Path).TrimEnd('\') -ieq (Resolve-RealPath $Other).TrimEnd('\')) { return $true }
    $id = Get-FolderId $Path
    return ($null -ne $id) -and ($id -eq (Get-FolderId $Other))
}

# Asks where the path really goes, not how it is spelled, so an alias of the parent is still under
# it, whether or not the folder itself exists yet. Two questions, because each sees what the other
# cannot: the real path takes out an 8.3 name (E:\STABLE~1), a junction and a subst drive; the ID
# of each folder above the path catches a share or a mapped drive that leads back to this PC.
function Test-PathUnder {
    param([string]$Path, [string]$Parent, [switch]$OrEqual)
    $full   = (Resolve-RealPath $Path).TrimEnd('\')
    $parent = (Resolve-RealPath $Parent).TrimEnd('\')
    if ($OrEqual -and $full.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($full.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }

    $parentId = Get-FolderId $Parent
    if (-not $parentId) { return $false }
    $folder = (Resolve-FullPath $Path).TrimEnd('\')
    if (-not $OrEqual) { $folder = [System.IO.Path]::GetDirectoryName($folder) }
    while ($folder) {
        if ((Get-FolderId $folder) -eq $parentId) { return $true }
        $folder = [System.IO.Path]::GetDirectoryName($folder.TrimEnd('\'))
    }
    return $false
}

# StoryForgeX processes started from this install folder. With -AnyVersion, every one started from a
# versioned folder under the root or through the current link - "the stable build". A Debug build
# from the repo is never among them.
function Get-AppProcess {
    param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot = $DefaultInstallRoot)
    $all = @(Get-CimInstance Win32_Process -Filter "Name = '$AppExeName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath })
    # Windows reports the exe path the way the process was started - through an 8.3 name, a
    # junction, a subst drive - so real paths are compared, never the spelling.
    if ($AnyVersion) {
        # Exactly our folders, not a neighbour whose name happens to start the same way
        # (StoryForgeX_old, StoryForgeX-backup): the current link itself, or a versioned folder, which
        # always has a dash and a version number after the app name.
        $current   = Get-CurrentLinkPath $InstallRoot
        $versioned = '^' + [regex]::Escape((Join-Path (Resolve-RealPath $InstallRoot) $AppName) + '-') + '\d[^\\]*\\'
        return @($all | Where-Object {
            (Test-PathUnder $_.ExecutablePath $current) -or
            ((Resolve-RealPath $_.ExecutablePath) -match $versioned)
        })
    }
    $exePath = Join-Path (Resolve-FullPath $InstallDir) $AppExeName
    return @($all | Where-Object { Test-SamePath $_.ExecutablePath $exePath })
}

# git with this script's error handling switched off for the call. Windows PowerShell 5.1, which the .cmd
# wrapper falls back to, turns a native command's stderr into an error record, and under
# $ErrorActionPreference = 'Stop' that ends the script with a stack trace. -Quiet drops stderr; without
# it the user sees git's own message. Check $LASTEXITCODE afterwards.
function Invoke-Git {
    param([string[]]$Arguments, [switch]$Quiet)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & git -C $RepoRoot @Arguments 2>$null }
        else        { & git -C $RepoRoot @Arguments }
    }
    finally { $ErrorActionPreference = $previous }
}

# The short commit of HEAD, or '' when git is missing or this is no checkout.
function Get-HeadCommit {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return '' }
    $commit = "$(Invoke-Git @('rev-parse', '--short', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0) { return '' }
    return $commit
}

# A Start Menu entry the user can pin to the taskbar with a right-click. Pinning itself
# cannot be scripted on Windows 11.
function Write-StartMenuShortcut {
    param([string]$InstallDir)
    $programs = [Environment]::GetFolderPath('Programs')
    $linkPath = Join-Path $programs $ShortcutName
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath       = Join-Path $InstallDir $AppExeName
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation     = (Join-Path $InstallDir $AppExeName) + ',0'
    $shortcut.Description      = 'StoryForge X'
    $shortcut.Save()
    return $linkPath
}

# Deletes the link itself and never what it points at. Remove-Item -Recurse on a junction
# has historically walked into the target and deleted the real files.
function Remove-DirectoryLink {
    param([string]$Path)
    [System.IO.Directory]::Delete($Path, $false)
}

# Where a directory link points, or $null for a real folder or nothing at all.
function Get-DirectoryLinkTarget {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['Target'] -and @($item.Target)[0]) { return @($item.Target)[0] }
    return $null
}

# The parsed install marker of a folder, or $null when there is none, it cannot be read, or it is
# another app's.
function Read-InstallMarker {
    param([string]$InstallDir)
    $path = Join-Path $InstallDir $InstallMarkerName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { $marker = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return $null }
    if ($null -eq $marker -or $marker.app -ne $AppName) { return $null }
    return $marker
}

# True only for a folder a previous build.ps1 published into. Everything destructive in these
# scripts is gated on this, so a folder we did not create is never cleaned. With -OneOff, only a
# one-off build's folder - a second line behind the path check, since a stable release is never a
# one-off's folder.
function Test-OurInstall {
    param([string]$InstallDir, [switch]$OneOff)
    $marker = Read-InstallMarker $InstallDir
    if ($null -eq $marker) { return $false }
    return (-not $OneOff) -or ("$($marker.version)" -match '-oneoff\.')
}

# True for a finished build. A stable folder is marked complete only once it has been made current;
# a one-off folder once its publish succeeded. Only a real true counts: a hand-edited "false" is
# not finished.
function Test-InstallComplete {
    param([string]$InstallDir)
    $marker = Read-InstallMarker $InstallDir
    return ($null -ne $marker) -and ($marker.complete -eq $true)
}

# True when the current link beside this folder points at it - the folder the Start Menu opens.
function Test-InstallCurrent {
    param([string]$InstallDir)
    $target = Get-DirectoryLinkTarget (Get-CurrentLinkPath (Split-Path -Parent $InstallDir))
    if (-not $target) { return $false }
    return (Test-SamePath $target $InstallDir)
}

# A stable build never republishes over a version folder that was made current. The current link and
# the Start Menu shortcut move to the new folder before the bump is committed and pushed, so a bump
# that is then lost hands out the same number again - and its folder is the one the shortcut opens,
# perhaps with StoryForgeX running from it. Both signs are asked, the marker and the link itself, so a
# run that died between moving the link and marking the folder is still caught. A folder that never
# became current - an interrupted publish, a link that could not be made - is neither, so build.ps1
# still cleans that one; unless somebody started StoryForgeX from it by hand, in which case the clean
# would stop at the locked exe with half the folder gone.
function Assert-StableTargetFree {
    param([string]$InstallDir, [string]$Version, [string]$Current)
    if ((Test-InstallComplete $InstallDir) -or (Test-InstallCurrent $InstallDir)) {
        Fail "$Version is already published in $InstallDir." @(
            "A finished stable folder is never republished over - it may be the version the Start Menu opens right now.",
            "Directory.Build.props still says $Current, so the bump of the run that built it never reached origin.",
            "Record it by hand - set <Version> to $Version in Directory.Build.props, commit that and push $ReleaseBranch",
            "(or open a PR for it, if $ReleaseBranch is protected) - then run this again."
        )
    }
    $running = @(Get-AppProcess $InstallDir)
    if ($running.Count -gt 0) {
        Fail "StoryForgeX is running from $InstallDir (PID $($running[0].ProcessId))." @(
            "That folder holds a build of $Version that was never made current, and this run would rebuild it.",
            "Close that StoryForgeX, then run the build again."
        )
    }
}

# Deletes an install folder with its marker last. The marker is what lets a later run clean the
# folder, so a delete that stops half-way - a file still held open - must not have taken it first.
# The name is compared, not the full path: Get-ChildItem writes an 8.3 name out long, so a full
# path built from C:\Users\LITTLE~1\... never matches.
function Remove-InstallFolder {
    param([string]$InstallDir)
    Get-ChildItem -LiteralPath $InstallDir -Force |
        Where-Object { $_.Name -ine $InstallMarkerName } |
        Remove-Item -Recurse -Force
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

function Write-InstallMarker {
    param([string]$InstallDir, [string]$Version = '', [switch]$Complete)
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    [ordered]@{
        app         = $AppName
        version     = $Version
        complete    = [bool]$Complete
        installedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        commit      = Get-HeadCommit
        note        = 'Written by scripts/build.ps1. Without this file the scripts refuse to clean this folder.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDir $InstallMarkerName) -Encoding UTF8
}

function Test-DirectoryHasContent {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return (@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count -gt 0)
}

# --- versioned stable builds ---------------------------------------------------------------

function Get-VersionedInstallDir {
    param([string]$InstallRoot, [string]$Version)
    return Join-Path $InstallRoot "$AppName-$Version"
}

# The fixed path the Start Menu shortcut and any taskbar pin target: E:\StableVersion\StoryForgeX, a
# junction build.ps1 points at the newest versioned folder, so a pin made months ago keeps opening the
# current build.
function Get-CurrentLinkPath {
    param([string]$InstallRoot = $DefaultInstallRoot)
    return Join-Path (Resolve-FullPath $InstallRoot) $AppName
}

# Points the fixed path at the folder just published: creates the junction, or retargets the one an
# earlier build made. A real folder of that name is left alone and reported - the shortcut then has to
# point at the versioned folder directly. Returns $true when the link now points at the target.
function Set-CurrentLink {
    param([string]$InstallRoot, [string]$Target)
    $linkPath = Get-CurrentLinkPath $InstallRoot
    $target   = (Resolve-FullPath $Target).TrimEnd('\')

    if (Test-Path -LiteralPath $linkPath) {
        $existingTarget = Get-DirectoryLinkTarget $linkPath
        if (-not $existingTarget) {
            Write-Warn "$linkPath is a real folder, not a link to a version - left alone"
            return $false
        }
        if ((Resolve-FullPath $existingTarget).TrimEnd('\') -ieq $target) { return $true }
        Remove-DirectoryLink $linkPath
    }

    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
    return $true
}

# Directory.Build.props is hand-written XML with comments in it that an XML round-trip would
# reformat, so it is read and edited as one string and only the number is ever touched.
function Get-PropsVersion {
    if (-not (Test-Path -LiteralPath $PropsPath)) {
        Fail "Cannot find $PropsPath." @("The version lives in its <Version> element.")
    }
    $found = [regex]::Matches([IO.File]::ReadAllText($PropsPath), '<Version>([^<]+)</Version>')
    if ($found.Count -ne 1) {
        Fail "Expected exactly one <Version> element in Directory.Build.props, found $($found.Count)."
    }
    $current = $found[0].Groups[1].Value.Trim()
    if ($current -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Fail "Cannot bump '$current' - expected a numeric version like 1.2.2 or 1.2.2.1."
    }
    return $current
}

# A bump zeroes everything to its right - 1.2.3.4 with -Part minor is 1.3.0, not 1.3.3.4 - and only
# 'build' keeps a fourth component, so a release bump drops back to three parts and starts counting
# builds again from there. The everyday case is 'build': 1.2.2 -> 1.2.2.1 -> 1.2.2.2.
function Get-NextVersion {
    param([string]$Current, [string]$Part)
    $n = @($Current -split '\.' | ForEach-Object { [int]$_ })
    while ($n.Count -lt 4) { $n += 0 }
    switch ($Part) {
        'major' { return '{0}.0.0' -f ($n[0] + 1) }
        'minor' { return '{0}.{1}.0' -f $n[0], ($n[1] + 1) }
        'patch' { return '{0}.{1}.{2}' -f $n[0], $n[1], ($n[2] + 1) }
        default { return '{0}.{1}.{2}.{3}' -f $n[0], $n[1], $n[2], ($n[3] + 1) }
    }
}

# The version a one-off -InstallDir build is stamped with, so the title bar or a log line can never
# pass it off as the stable build of the same number. The 'g' in front of the hash is load-bearing:
# a short hash can be all digits with a leading zero (0123456, about 1 commit in 270), which is not a
# valid SemVer prerelease identifier, and NuGet's restore then fails with nothing but MSB4181.
#
# -Dirty adds '.dirty': the build holds changes that are not in that commit, so checking the commit
# out would not give back the code that was built.
function Get-OneOffVersion {
    param([string]$Current, [string]$Commit, [switch]$Dirty)
    if (-not $Commit) { return "$Current-oneoff.nogit" }
    $suffix = if ($Dirty) { '.dirty' } else { '' }
    return "$Current-oneoff.g$Commit$suffix"
}

# True when the working tree has uncommitted or untracked files, and also when git cannot say: a
# stamp must not claim a build matches its commit unless that is known.
function Test-UncommittedChanges {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $true }
    $changes = @(Invoke-Git @('status', '--porcelain') -Quiet)
    return ($LASTEXITCODE -ne 0) -or ($changes.Count -gt 0)
}

# Writes the string back byte for byte apart from the number: same line endings, same BOM or lack of
# one, so the bump commit is a one-line diff.
function Set-PropsVersion {
    param([string]$Version)
    $bytes  = [IO.File]::ReadAllBytes($PropsPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text   = [IO.File]::ReadAllText($PropsPath)
    $m      = [regex]::Match($text, '<Version>[^<]+</Version>')
    if (-not $m.Success) {
        # Validated before the publish, but that was minutes ago and the file may have been edited since.
        Fail "Directory.Build.props no longer has a <Version> element - it changed while the build ran." @(
            "The published $Version folder is fine. Set the version to $Version by hand, then commit and push it."
        )
    }
    $text   = $text.Remove($m.Index, $m.Length).Insert($m.Index, "<Version>$Version</Version>")
    [IO.File]::WriteAllText($PropsPath, $text, (New-Object Text.UTF8Encoding $hasBom))
}

# The bump Push-VersionBump made and could not push, looked for among the commits this checkout is
# ahead of origin. That commit belongs to a build that was published, so the advice for it is to
# push it - resetting it away is what hands the same version number out twice. Returns $null when
# there is none; otherwise its short hash, and whether it is the only thing ahead.
function Get-UnpushedBump {
    $commits = @(Invoke-Git @('log', '--format=%h %s', "origin/$ReleaseBranch..HEAD") -Quiet)
    if ($LASTEXITCODE -ne 0) { return $null }
    $bump = @($commits | Where-Object { $_ -match '^\S+ chore: bump version to \d' }) | Select-Object -First 1
    if (-not $bump) { return $null }
    $commit = ($bump -split ' ')[0]
    # The bump touches one file. A commit that only borrowed the subject line is not it.
    $files = @(Invoke-Git @('show', '--name-only', '--format=', $commit) -Quiet | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0 -or $files.Count -ne 1 -or $files[0] -ne 'Directory.Build.props') { return $null }
    return [pscustomobject]@{ Commit = $commit; Alone = ($commits.Count -eq 1) }
}

# Everything that has to be true before a build may go into the stable root. This refuses rather
# than asks: a yes/no here would put a "stable" build of unmerged code on disk, stamped with a
# version number main is about to hand out again to a different build.
function Assert-ReleaseReady {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git is not on your PATH." @("The stable build commits and pushes the version bump, so it needs git.")
    }

    $branch = "$(Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        Fail "$RepoRoot is not a git checkout."
    }
    if ($branch -ne $ReleaseBranch) {
        Fail "You are on branch '$branch', not $ReleaseBranch." @(
            "Stable builds are only made from $ReleaseBranch, so the version bump lands where every later build sees it.",
            "Merge the branch, then:  git checkout $ReleaseBranch; git pull",
            "To try this branch's build, give it a folder of its own outside $DefaultInstallRoot - nothing is bumped or pushed:",
            "  .\scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
        )
    }

    # Untracked files count too: the SDK compiles every *.cs in a project folder whether git knows it or
    # not, so a new file that was never added would go into a build stamped with a commit that
    # does not contain it. bin\, obj\ and artifacts\ are ignored, so they never show up here.
    # The exit code decides, not the output: a git that fails here prints nothing to stdout, and
    # nothing reads as "no changes".
    $dirty = @(Invoke-Git @('status', '--porcelain') -Quiet)
    if ($LASTEXITCODE -ne 0) {
        Fail "git status failed (exit code $LASTEXITCODE), so the working tree could not be checked." @(
            "Run git status yourself to see why - a damaged index or a safe.directory refusal are the usual causes."
        )
    }
    if ($dirty.Count -gt 0) {
        Fail "The working tree has uncommitted or untracked files." @(
            @("A stable build has to match a commit on $ReleaseBranch. Commit, stash, ignore or delete these first:") +
            @($dirty | Select-Object -First 8 | ForEach-Object { "  $_" })
        )
    }

    Write-Note "fetching origin/$ReleaseBranch"
    Invoke-Git @('fetch', '--quiet', 'origin', $ReleaseBranch)
    if ($LASTEXITCODE -ne 0) {
        Fail "git fetch failed." @("Check your network and GitHub login, then run this script again.")
    }

    # Both directions. Behind means the build would not be of what is on origin; ahead means the
    # push at the end would carry commits onto $ReleaseBranch that never went through a PR.
    $counts = "$(Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...origin/$ReleaseBranch") -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not ($counts -match '^(\d+)\s+(\d+)$')) {
        Fail "Could not compare $ReleaseBranch with origin/$ReleaseBranch." @("Does the remote 'origin' have a '$ReleaseBranch' branch?")
    }
    $ahead  = [int]$Matches[1]
    $behind = [int]$Matches[2]
    # Asked before "behind", whose plain git pull would wrap the bump in a merge commit.
    $bump = if ($ahead -gt 0) { Get-UnpushedBump } else { $null }
    if ($bump -and $bump.Alone) {
        Fail "The version bump of the last stable build was never pushed." @(
            "$ReleaseBranch is one commit ahead of origin/$ReleaseBranch, and that commit is the bump. Do not reset it away:",
            "the build it belongs to was published, and a reset hands the same version number out again.",
            "Push it:  git pull --rebase origin $ReleaseBranch; git push origin $ReleaseBranch",
            "If $ReleaseBranch is protected, open a PR for the bump commit instead. Then run this script again."
        )
    }
    if ($bump) {
        Fail "The version bump of the last stable build was never pushed, and other commits sit beside it." @(
            "$ReleaseBranch is $ahead commits ahead of origin/$ReleaseBranch. One of them ($($bump.Commit)) is the bump. Do not lose it:",
            "the build it belongs to was published, and a reset hands the same version number out again.",
            "Keep the other commits on a branch, leave only the bump on $ReleaseBranch, and push it:",
            "  git branch feature/<name>; git reset --hard origin/$ReleaseBranch; git cherry-pick $($bump.Commit); git push origin $ReleaseBranch",
            "If $ReleaseBranch is protected, open a PR for the bump commit instead. Then run this script again."
        )
    }
    if ($behind -gt 0) {
        Fail "$ReleaseBranch is $behind commit(s) behind origin/$ReleaseBranch." @("git pull, then run this script again.")
    }
    if ($ahead -gt 0) {
        Fail "$ReleaseBranch is $ahead commit(s) ahead of origin/$ReleaseBranch." @(
            "A stable build is of code that is already on origin, and this script must not be the thing that pushes other commits.",
            "Push them through a PR, or move them to a branch:  git branch feature/<name>; git reset --hard origin/$ReleaseBranch"
        )
    }
    Write-Ok "on $ReleaseBranch, clean, level with origin"
}

# Commits Directory.Build.props and nothing else, then pushes. Called only after the publish
# succeeded, so a broken build never moves the number.
function Push-VersionBump {
    param([string]$Version)
    Invoke-Git @('commit', '--quiet', '-m', "chore: bump version to $Version", '--', 'Directory.Build.props')
    if ($LASTEXITCODE -ne 0) {
        Fail "The build is done, but committing the version bump failed." @(
            "Directory.Build.props already says $Version. Commit and push it by hand:",
            "  git commit -m `"chore: bump version to $Version`" -- Directory.Build.props; git push origin $ReleaseBranch"
        )
    }
    # The freshness check ran before a build that takes minutes; the likeliest reason a push
    # fails now is that origin moved meanwhile, and a plain retry would be rejected the same way.
    Invoke-Git @('push', '--quiet', 'origin', $ReleaseBranch)
    if ($LASTEXITCODE -ne 0) {
        Fail "The build is done and the bump to $Version is committed, but the push was rejected." @(
            "If origin/$ReleaseBranch moved while the build ran:  git pull --rebase origin $ReleaseBranch; git push origin $ReleaseBranch",
            "If it was the network or your login, fix that and:  git push origin $ReleaseBranch",
            "If $ReleaseBranch is protected, open a PR for the bump commit instead."
        )
    }
}
