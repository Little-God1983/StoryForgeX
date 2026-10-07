# Scripts

The build script for StoryForge X. `build.ps1` (or double-click `build.cmd`) makes the next stable
version in `E:\StableVersion`, the same way CodeSwitchX, ContentAutomatorX and RawCutX do. Full help:
`Get-Help .\scripts\build.ps1 -Full`.

## build.ps1 / build.cmd

The stable build:

1. Bumps the version.
2. Builds in Release and runs the tests. A red suite stops everything before anything is published.
3. Publishes into `E:\StableVersion\StoryForgeX-<version>`.
4. Points the `E:\StableVersion\StoryForgeX` junction at the new folder and writes a `StoryForge X`
   Start Menu shortcut through that junction, so a taskbar pin always opens the newest build.
5. Commits the one-line bump to `Directory.Build.props` and pushes `main`.

Older versions stay beside the new one; delete them by hand when you like. The version is only
written back once the publish succeeds, so the recorded number never runs ahead of a build that
broke halfway through.

The app keeps its database, settings and reference files in `%LOCALAPPDATA%\StoryForgeX`, whichever
version runs. A new version opens the same projects as the old one.

It only runs from a `main` that has no uncommitted or untracked files and is level with origin
(neither behind nor ahead). On a feature branch it stops before building anything: the bump would
land on the branch, the next branch would hand out the same number, and the stable folder would
hold unmerged code. To try a branch's build, give it an `-InstallDir`. That is a one-off build
stamped `<version>-oneoff.g<commit>`, with `.dirty` on the end when it holds uncommitted changes:
nothing is bumped, committed or pushed, and no shortcut is written.

A running StoryForge X is not closed, because it may be in the middle of a run. It keeps running the
version it was started from, and the script says so at the end. Close it before you start the new
version: both use the same database, and a new version may update it when it starts. The one exception is a one-off build
into a folder StoryForge X is running from: that build refuses to start until you close it.

A stable folder that was made current is never republished over. If the bump of the last build was
committed but not pushed (a rejected push, say), the next build stops and tells you to push it. If
that commit is gone and the next number's folder already exists, the build stops and tells you to
record that version by hand.

A `build.cmd` double-clicked in Explorer waits for a key when it ends, so the window stays readable.
Started from PowerShell, Git Bash, an open Command Prompt or a task it only waits after a failure.
Another file manager is not Explorer, so a double click there closes the window after a good build.
Set `STORYFORGEX_BUILD_PAUSE=never` for a caller that must never wait, or `=always` to force the wait.

Every folder the script publishes into gets a `storyforgex-install.json` marker. Nothing is ever
deleted from a folder without one, so a mistyped `-InstallDir` cannot take out a neighbouring release.

The published folder is framework-dependent: the machine needs the .NET 10 desktop runtime. It
holds `StoryForge.App.exe` and `StoryForge.ResearchServer.exe`, which the app starts for every
Research run. A publish that lacks either one stops the build before anything is bumped.

| Parameter | What it does |
|---|---|
| `-Part major\|minor\|patch\|build` | Which part to bump. Default `build`: `0.1.0 -> 0.1.0.1`, the "another build of the same release" case. A release bump zeroes everything to its right and drops the fourth part: `0.1.0.4 -Part minor -> 0.2.0`. |
| `-InstallDir <path>` | A one-off build into a folder of your own, from any branch. Must be outside the repo and the stable root. |
| `-Clean` | With `-InstallDir`: wipe that folder first (only if it carries the marker). |
| `-SkipTests` | Skip the test run (for when you just ran it). |
| `-NoShortcut` | Leave the Start Menu shortcut as it is. |

```powershell
.\scripts\build.ps1                                                     # 0.1.0 -> 0.1.0.1, build, test, publish, commit, push
.\scripts\build.ps1 -Part minor                                         # 0.1.0.1 -> 0.2.0, an actual release
.\scripts\build.ps1 -InstallDir E:\Builds\StoryForgeX-test -SkipTests   # try this branch, repo untouched
```

Tags stay manual. The script prints the command at the end:

```powershell
git tag v<next>; git push origin v<next>
```
