---
name: csharp-conventions
description: Use when writing, changing or reviewing C# or XAML in StoryForge X - WPF views, ViewModels with CommunityToolkit.Mvvm, engine services and DI registration, EF Core (SQLite) entities or migrations, xUnit tests, NuGet package changes, async/threading or error handling in this repo.
---

# StoryForge X C# conventions

## Overview

House style for the StoryForge X solution (.NET 10, C# latest, WPF, CommunityToolkit.Mvvm,
Generic Host, EF Core SQLite, xUnit 2). Every example below is taken from, or matches, code in this
repo. The build enforces most of the style through `.editorconfig` with `TreatWarningsAsErrors`.

**Core rule:** the UI knows the engine only through `IStoryForgeClient`
(`StoryForge.Client`). `ArchitectureTests` fails if an App type outside `App` names an engine type.

## Quick reference

| Topic | Do |
|---|---|
| Namespaces | file-scoped, matching the folder: `StoryForge.App.ViewModels.Pages` |
| Fields | `_camelCase`; private `const` / `static readonly` in `PascalCase`; never `this.` |
| Types | `sealed` by default; engine types `internal sealed`; data as `record` |
| Constructors | primary constructors for services and small ViewModels |
| Collections | `[]` / `[a, b]` collection expressions; `IReadOnlyList<T>` on public surfaces |
| Observable state | `[ObservableProperty] private T _name;` in a `sealed partial` class |
| Commands | `[RelayCommand]` on a private method; `Task`-returning for async; `CanExecute = nameof(...)` |
| Async | `Async` suffix, `CancellationToken cancellationToken` last (`= default` on the client interface) |
| UI thread | `UiThread.Run(...)` for anything raised by the engine |
| A button that fails | catch all, `Debug.WriteLine` the exception, show `Could not <what>: <message>` in the screen's red `TextBlock` |
| Load / background failures | the same, or a comment saying why the user is not told |
| Comments | say *why*, in plain words, with the concrete case (`"15:40:02"`, `#78`) |
| Tests | `public sealed class XTests`; method names are sentences: `Saved_settings_survive_a_restart` |

## ViewModel

`sealed partial`, inherit `ObservableObject` (pages inherit `PageViewModel`), depend on
`IStoryForgeClient`. Derived properties use `NotifyPropertyChangedFor`; command availability uses
`NotifyCanExecuteChangedFor`. Always `nameof`, never a string.

```csharp
namespace StoryForge.App.ViewModels;

/// <summary>One status pill in the top bar, e.g. "ComfyUI · ok"; the reason shows as its tooltip.</summary>
public sealed partial class ProviderPillViewModel(ProviderId id, string name) : ObservableObject
{
    public ProviderId Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private string _name = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private ProviderState _state;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    private bool _checked;

    public string Text => $"{Name} · {(Checked ? ProviderStateText.For(State) : "checking…")}";

    // trimmed: Update(status, checkedYet) sets the four properties
}
```

Commands, from `AssemblyViewModel` (members picked from across the file):

```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(EarlierCommand), nameof(LaterCommand))]
private int _selectedCut = -1;

public bool HasSelection => SelectedCut >= 0 && SelectedCut < _draft.Count && SelectedCut + 1 < Blocks.Count;

[RelayCommand(CanExecute = nameof(HasSelection))]
private void Earlier() => Move(-0.1);

[RelayCommand(CanExecute = nameof(CanApprove))]
private Task ApproveAsync() => ActAsync("approve the video", () => client.ApproveAsync(ProjectId, PipelineStage.Assembly, Version!.Value));
```

A large ViewModel is split into partial files by feature:
`ScriptMatrixViewModel.cs`, `ScriptMatrixViewModel.Stills.cs`, `ScriptMatrixViewModel.Rerun.cs`.

Engine events arrive on engine threads. Marshal them once, at the subscription:

```csharp
client.StageUpdated += (_, update) => UiThread.Run(() => Updating = ReceiveAsync(update));
```

`UiThread.Run` runs inline when there is no `Application` (tests), so ViewModels stay testable.

## View ↔ ViewModel wiring

Implicit data templates in `MainWindow.xaml` pick the view for each page ViewModel. Views never set
their own `DataContext`; they declare a design-time one.

```xml
<!-- MainWindow.xaml -->
<DataTemplate DataType="{x:Type pages:SettingsPageViewModel}">
  <views:SettingsView />
</DataTemplate>
```

```xml
<!-- Views/AssemblyView.xaml -->
<UserControl ...
             d:DataContext="{d:DesignInstance Type=asm:AssemblyViewModel}">
  <UserControl.Resources>
    <BooleanToVisibilityConverter x:Key="Visible" />
  </UserControl.Resources>
  ...
  <TextBlock Text="{Binding Stale}" ...
             Visibility="{Binding Stale, Converter={views:NullToCollapsedConverter}}" AutomationProperties.AutomationId="AssemblyStale" />
  <TextBlock Text="{Binding Error}" FontSize="13" Foreground="{StaticResource ErrorTextBrush}" TextWrapping="Wrap" Margin="0,0,0,10"
             AutomationProperties.AutomationId="AssemblyRefused">
    ...
```

- Colours and styles come from `Themes/Theme.xaml` (`TextBrush`, `AccentBrush`, `PrimaryButtonStyle`,
  `FieldStyle`, …). New XAML takes its colours from there; add a brush to the theme when one is missing.
  Six views still carry literal hex values for single bars and pills (about 125, most of them in
  `ScriptMatrixView.xaml`). They are older code: don't copy them, and don't convert them in passing.
- New converters go in `Views/Converters.cs` as `sealed class XConverter : MarkupExtension, IValueConverter`
  with `ProvideValue => this`, used inline as `{views:XConverter}`. `ConvertBack` throws `NotSupportedException`.
- Every input and button gets `AutomationProperties.Name` (or `AutomationId` for text that tests find).
- Text inputs bound live use `UpdateSourceTrigger=PropertyChanged`; saving is debounced in the ViewModel.

Code-behind is allowed only for what WPF cannot bind, with a comment saying why:
`PasswordBox.Password`, `OpenFolderDialog`, the Ctrl+click selection guard, `Closing` flush.
Code-behind calls into the ViewModel; it holds no logic of its own.

## Service with DI

Engine service: `internal sealed`, primary constructor, async with a cancellation token, a fresh
`DbContext` per operation from the factory.

```csharp
namespace StoryForge.Engine.Settings;

/// <summary>Reads and writes <see cref="EngineSettings"/>. Each group is its own row.</summary>
internal sealed class SettingsStore(IDbContextFactory<StoryForgeDbContext> contextFactory)
{
    private const string PathsKey = "paths";

    public async Task<EngineSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.Settings.AsNoTracking()
            .ToDictionaryAsync(e => e.Key, e => e.Json, cancellationToken);
        ...
    }
}
```

Register it in `StoryForge.Engine/ServiceCollectionExtensions.cs` (`AddStoryForgeEngine`); engine
services are singletons. Anything that reaches an external tool (Claude CLI, ComfyUI, ffmpeg,
Resolve, the web) sits behind an interface (`IComfyUi`, `IAudioConverter`, `IScriptAgent`, …) so
`EngineTestHost` can replace it. Time comes from the injected `TimeProvider`, not `DateTime.Now`.

New capability for the UI: add the method to `IStoryForgeClient` (with an `<exception>` doc for every
refusal), its records to `StoryForge.Client`, the implementation to `InProcessStoryForgeClient`, and a
stub to `FakeStoryForgeClient` in `StoryForge.App.Tests`.

App services are registered in `App.OnStartup` on the Generic Host builder. Page ViewModels are built
by `MainViewModel`, not resolved from the container.

## EF Core

- `StoryForgeDbContext` in `StoryForge.Engine/Data`, entities named `XEntry`. SQLite at
  `%LOCALAPPDATA%\StoryForgeX`. `DatabaseInitializer` (a hosted service) migrates on start.
- New migration: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project StoryForge.Engine --output-dir Data/Migrations`
  (`DesignTimeDbContextFactory` serves the tooling). Migrations are generated code; never edit an applied one.
- Package versions: `Microsoft.EntityFrameworkCore.*`, `Microsoft.Extensions.*` and the `dotnet-ef`
  tool all stay on the same `10.0.x`.

## Error handling

There is no `ILogger`. Do not add one without asking. Errors flow like this:

1. **Refusals** the user can act on: the engine throws `ArgumentException`,
   `InvalidOperationException` or `KeyNotFoundException` with a message written for the user
   (`"ComfyUI port must be between 1 and 65535."`).
2. **A stage that cannot produce a result** throws `StageFailedException(reason)`; the runner stores
   the reason and the cell shows it.
3. **A button** (Approve, Save, Render again, …) catches everything, so no click fails silently and
   nothing is left for the last-resort message box. The full exception goes to `Debug.WriteLine`,
   and the screen's red text says `Could not <what>: <message>`. A refusal and a surprise look the
   same on screen; the engine's message after the colon tells them apart. Each ViewModel with
   several buttons has one helper for this, from `AssemblyViewModel`
   (`ScriptMatrixViewModel.ActAsync` has the same shape):

```csharp
private async Task ActAsync(string what, Func<Task> act)
{
    try
    {
        Error = null;
        _wanted = null;
        await act();
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Could not {what}: {ex}");
        Error = $"Could not {what}: {ex.Message}";
        return;
    }
    HasEdits = false;
    await LoadAsync();
}
```

   `what` finishes the sentence "Could not …": lower case, the verb and its object
   (`"approve the video"`, `"save the shot"`, `"cut the video again"`). A command with a single call
   carries the same try/catch in its own body.

   Forms word one case differently, and still catch everything. When the engine answers a filled-in
   form with an `ArgumentException` (a name that is taken, a port out of range), the message is
   shown as it is, next to the form; anything else gets the lead-in:
   `CreateError = ex is ArgumentException ? ex.Message : $"Could not create the profile: {ex.Message}";`
   That is how `ProfilesPageViewModel.CreateAsync`, `NewProjectPageViewModel.StartAsync` and the
   save in `SettingsPageViewModel` read. `ProfileEditorViewModel.TestRenderAsync` shows the bare
   message for every failure. Match the screen you are in; don't unify these in passing.

4. **Loads and background work** (a screen loading, timers, event listeners, fire-and-forget) catch
   everything too, write the full exception with `Debug.WriteLine`, and either say so on screen or
   comment why the user is not told:

```csharp
catch (Exception ex)
{
    Debug.WriteLine($"Loading the Assembly of {ProjectId} failed: {ex}");
    Error = $"Could not load the Assembly: {ex.Message}";
}
```

```csharp
catch (Exception ex)
{
    // The next tick tries again; a message box every 30 s would be worse than none.
    Debug.WriteLine($"Provider status refresh failed: {ex}");
}
```

5. `App.DispatcherUnhandledException` shows a message box as the last resort; don't rely on it.

Progress that the user should see goes through `IProgress<ActivityLine>` on the `StageContext`.

## Unit tests

xUnit 2 (`[Fact]`, `[Theory]`/`[InlineData]`), `Xunit` is a global using. Arrange / act / assert
separated by blank lines, no comments needed for them.

App tests drive ViewModels against `FakeStoryForgeClient`, or a small private fake for one case:

```csharp
public sealed class ProviderStatusBoardTests
{
    [Fact]
    public async Task A_refresh_publishes_the_latest_statuses()
    {
        var client = new FakeStoryForgeClient();
        client.Providers.Add(new(ProviderId.Ffmpeg, "FFmpeg", ProviderState.Ok, "ffmpeg version 6.1.1"));
        var board = new ProviderStatusBoard(client);
        var raised = 0;
        board.StatusesChanged += (_, _) => raised++;

        await board.RefreshAsync();

        Assert.Equal(ProviderState.Ok, board.Get(ProviderId.Ffmpeg)?.State);
        Assert.Equal(1, raised);
    }
}
```

Engine tests run a real host on a temp folder through `EngineTestHost`, which swaps every external
tool for a fake; pass `replace` to put in your own:

```csharp
public sealed class SettingsTests : IDisposable
{
    private readonly EngineTestHost _engine = new();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task Saved_settings_survive_a_restart()
    {
        var first = await _engine.StartClientAsync();
        await first.SaveSettingsAsync(Changed());

        var second = await _engine.StartClientAsync();

        Assert.Equal(Changed(), await second.GetSettingsAsync());
    }
}
```

Large test classes are split into partial files the same way as ViewModels (`StillsRunTests.Clips.cs`).

## Anti-patterns

| Don't | Do instead |
|---|---|
| Logic in code-behind (`Button_Click` that computes or calls the client) | `[RelayCommand]` in the ViewModel; code-behind only for unbindable controls |
| `.Result`, `.Wait()`, `GetAwaiter().GetResult()` | `await`. The one exception is `App.OnExit` stopping the host via `Task.Run`, which is commented |
| Setting a ViewModel property from an engine event handler directly | `UiThread.Run(() => ...)` at the subscription |
| `OnPropertyChanged("Text")`, `CanExecute = "HasSelection"` | `nameof(Text)`, `nameof(HasSelection)` |
| App code naming an engine type (`SettingsStore`, `PipelineRunner`) | Go through `IStoryForgeClient`; extend it if needed |
| A button whose failure can escape (no catch, or a catch for some exception types only) | Catch everything: `Debug.WriteLine` the exception and show `Could not <what>: <message>`, through the ViewModel's `ActAsync` || An empty `catch` | `Debug.WriteLine($"... failed: {ex}")` plus an on-screen message or a comment saying why there is none |
| Calling real Claude CLI / ComfyUI / ffmpeg / the web in a test | A fake behind the interface, via `EngineTestHost` |
| A new hard-coded colour in a view | A brush from `Theme.xaml`; add one if it is missing |
| A new library, MVVM pattern or logging framework | Ask first |
| Editing `<Version>` in `Directory.Build.props` | Left to `scripts/build.ps1` |

## Common mistakes

- **Build fails on a style warning**: unused `using` (IDE0005), unused parameter (IDE0060), field without `_`.
  Fix the code; don't lower the severity in `.editorconfig`.
- **XAML errors** only show in `dotnet build` output, not in the LSP. Build after every `.xaml` change.
- **`CS2001 … .g.cs could not be found`**: another process built the same `obj` at once. Build again.
- **Forgot the fake**: a new `IStoryForgeClient` member breaks `FakeStoryForgeClient` and every private
  fake in `StoryForge.App.Tests` that implements the interface.
