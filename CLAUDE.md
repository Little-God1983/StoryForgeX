# StoryForge X

A WPF desktop app that turns a topic into a narrated video through a staged pipeline (Research →
Script → Voice → Storyboard → References → Stills → Clips → Assembly), driving Claude CLI, ComfyUI,
ffmpeg and DaVinci Resolve. Long-form conventions with code examples: the `csharp-conventions`
skill (`.claude/skills/csharp-conventions/SKILL.md`).

**Spec and decisions** live outside the code: the claude.ai Design canvas
https://claude.ai/artifact/PjhEHQtoisZtPRSuQKLsFV (screen mockups plus the decision log; build
against its Settled column; it opens only from the owner's own account), and `.superpowers/` (specs
and plans per issue; `pending-canvas-decisions.md` holds decisions not yet written onto the canvas).

## Solution layout (`StoryForgeX.slnx`, projects next to it, no `src/`)

| Project | TFM / output | Role |
|---|---|---|
| `StoryForge.App` | `net10.0-windows`, WinExe, WPF | UI: Views, ViewModels, theme. `App.xaml.cs` is the composition root. |
| `StoryForge.Client` | `net10.0`, library | The contract: `IStoryForgeClient` plus the records it passes. No dependencies. |
| `StoryForge.Engine` | `net10.0`, library | Pipeline stages, EF Core (SQLite) data, providers. Implements the client in-process. |
| `StoryForge.Sources` | `net10.0`, library | Reads a project's sources (web pages, MediaWiki). |
| `StoryForge.ResearchServer` | `net10.0`, Exe | MCP server (`ModelContextProtocol`) that Claude CLI starts for Research. App references it only so its exe ships beside the app. |
| `StoryForge.App.Tests` | `net10.0-windows`, xUnit | View-model, converter, architecture and build-script tests against `FakeStoryForgeClient`. |
| `StoryForge.Engine.Tests` | `net10.0`, xUnit | Engine tests on a real host and temp SQLite via `EngineTestHost`, with fakes for every external tool. |

**Hard rule:** the App talks to the engine only through `IStoryForgeClient`. Only `App`
(the composition root) may name an engine type; `ArchitectureTests` fails the build otherwise.

## Build and test

```powershell
dotnet build StoryForgeX.slnx                    # ~15 s warm; just build the whole solution
dotnet test StoryForge.Engine.Tests --no-build --filter "FullyQualifiedName~SettingsTests"
dotnet test StoryForge.App.Tests --no-build --filter "FullyQualifiedName~ScriptMatrixTests"
dotnet test StoryForgeX.slnx --no-build          # full suite: ~1,400 tests, ~2.5 min
```

After every change: build, then run the tests of the area you touched, before reporting back.
Run the full suite before a commit that ends a task. CI (`.github/workflows/ci.yml`) builds and
tests in Release on `windows-latest`.

`TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on (`Directory.Build.props`), so any
`.editorconfig` rule at `warning` breaks the build: unused usings (IDE0005), block namespaces,
`this.`, misnamed fields, unused parameters.

## Language and settings

- .NET 10, `LangVersion latest` (C# 14), `Nullable` and `ImplicitUsings` enabled everywhere.
- File-scoped namespaces (enforced), primary constructors, collection expressions `[...]`,
  records for data, `required`/`init` where they fit.
- Fields `_camelCase`, private constants and `static readonly` fields `PascalCase`.
- Engine types are `internal sealed` by default (`InternalsVisibleTo` the test project).
- No `ILogger` in the codebase. Errors reach the user as plain-language messages; see the skill.

## WPF / MVVM

- **CommunityToolkit.Mvvm 8.4**: `[ObservableProperty]` on `_camelCase` fields (not partial
  properties), `[RelayCommand]` on methods. ViewModels are `sealed partial`, large ones are split
  into `Name.Feature.cs` partial files.
- **DI**: Generic Host (`Host.CreateApplicationBuilder`) in `App.OnStartup`. Engine services in
  `StoryForge.Engine/ServiceCollectionExtensions.cs` (`AddStoryForgeEngine`), app services in
  `App.xaml.cs`. Page ViewModels are created by `MainViewModel`, not by the container.
- **View ↔ ViewModel**: implicit `DataTemplate DataType=` per page ViewModel in `MainWindow.xaml`;
  views set `d:DataContext` for design-time only. Only `MainWindow` sets `DataContext` in code.
- **Threading**: engine events arrive on engine threads; marshal with `UiThread.Run(...)`.
  Never `.Result`/`.Wait()`; no `ConfigureAwait` in the app.
- **Code-behind**: only for what WPF cannot bind (`PasswordBox`, folder dialogs, selection
  guards), each with a comment saying why.
- **Styling**: one `Themes/Theme.xaml` (brushes `…Brush`, styles `…Style`); converters live in
  `Views/Converters.cs` as `MarkupExtension` converters. Controls get `AutomationProperties.Name`.

## Where things go

- New screen: `ViewModels/Pages/XPageViewModel.cs` (or a feature folder under `ViewModels/`),
  `Views/XView.xaml(.cs)`, a `DataTemplate` in `MainWindow.xaml`.
- New engine feature: a folder per stage under `StoryForge.Engine/` (`Voice/`, `Stills/`, …),
  registered in `AddStoryForgeEngine`, exposed through `IStoryForgeClient` plus records in `StoryForge.Client`.
- Tests: `StoryForge.<Project>.Tests/<Thing>Tests.cs`, flat, test names as sentences with underscores.

## Gotchas

- **EF migrations** are generated (`Data/Migrations`, treated as generated code). Create them with
  `dotnet tool restore` then `dotnet ef migrations add <Name> --project StoryForge.Engine --output-dir Data/Migrations`.
  Never hand-edit an applied migration.
- **`<Version>` in `Directory.Build.props`** is bumped only by `scripts/build.ps1` (stable build).
  Don't edit it by hand; feature branches never bump it.
- **`*.ps1`/`*.cmd` stay CRLF** (`.gitattributes`); `BuildScriptTests` cover the build script.
- **`CS2001 … .g.cs could not be found`** on a WPF build means another process (VS Code's language
  server, Visual Studio) was building the same `obj` at once. Run the build again before debugging.
- **`Engine/Resolve/send_to_resolve.py`** is copied to the output and run in Resolve's Python.
- Tests never touch real providers or `%LOCALAPPDATA%\StoryForgeX`; `EngineTestHost` uses a temp
  folder and swaps every external tool for a fake. Keep it that way.
- `docs/ai-rules-map.html` is a snapshot (from #89) of every prompt rule the stages send to the AI;
  the prompts in the engine are the source of truth.
