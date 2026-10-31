using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Assembly;
using StoryForge.App.ViewModels.References;
using StoryForge.App.ViewModels.Research;
using StoryForge.App.ViewModels.Script;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>One stage chip in the project header, e.g. "Stills" with its state.</summary>
public sealed record StageChip(PipelineStage Stage, string Name, StageState State)
{
    public string StateText => State switch
    {
        StageState.NotStarted => "not started",
        StageState.NeedsReview => "review",
        _ => State.ToString().ToLowerInvariant(),
    };

    /// <summary>"Research", "Research – review": the chip's text, as on the canvas.</summary>
    public string Label => State is StageState.NotStarted or StageState.Approved ? Name : $"{Name} – {StateText}";

    /// <summary>Research opens the fact sheet; Script, Voice, Board, Stills and Clips the matrix; References and Assembly their own screens; Publish arrives with its issue.</summary>
    public bool CanOpen => Stage != PipelineStage.Publish;

    // What screen readers announce.
    public override string ToString() => $"{Name}: {StateText}";
}

/// <summary>
/// The Result matrix screen for the open project: its header (name, aspect, resolution, target
/// length, stage chips) and the matrix: one row per segment of the script, with its cells by stage.
/// The Research chip opens the fact sheet in the same place.
/// </summary>
public sealed partial class ResultMatrixPageViewModel : PageViewModel
{
    private readonly IStoryForgeClient _client;
    private readonly Action<string> _openUrl;
    private readonly Func<IAudioPlayer>? _newPlayer;
    private readonly Func<string?> _pickPicture;
    private readonly Func<string, bool> _confirm;

    /// <param name="openUrl">Opens a source page in the browser; tests pass their own.</param>
    /// <param name="newPlayer">Makes the player a project's voice is played with; tests pass their own or none.</param>
    /// <param name="pickPicture">Asks for a picture file ("Add my own picture…"); null when none was picked. Tests pass their own.</param>
    /// <param name="confirm">Asks yes or no before a change that puts results out of date; tests pass their own.</param>
    public ResultMatrixPageViewModel(
        IStoryForgeClient client, Action<string>? openUrl = null, Func<IAudioPlayer>? newPlayer = null, Func<string?>? pickPicture = null, Func<string, bool>? confirm = null)
        : base("Result matrix", "Projects / Result matrix", "No project open. Start one from New project.")
    {
        _client = client;
        _openUrl = openUrl ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        _newPlayer = newPlayer;
        _pickPicture = pickPicture ?? PickPicture;
        _confirm = confirm ?? Confirm;
        client.StageUpdated += (_, update) => UiThread.Run(() => Updating = ReceiveAsync(update));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Name), nameof(Summary), nameof(Stages), nameof(ShowsMatrix))]
    private Project? _project;

    public bool HasProject => Project is not null;

    public string Name => Project?.Setup.Name ?? "";

    /// <summary>"16:9 · 1920×1080 · target 4:00".</summary>
    public string Summary => Project?.Setup.Output is { } o
        ? $"{o.Aspect} · {o.Width}×{o.Height} · target {Duration(o.TargetSeconds)}"
        : "";

    public IReadOnlyList<StageChip> Stages => Project?.Stages.Select(s => new StageChip(s.Stage, ChipName(s.Stage), s.State)).ToList() ?? [];

    /// <summary>Why the last project could not be opened or started; null when all is well.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>The fact sheet, while it is on screen in place of the matrix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFactSheet), nameof(ShowsMatrix), nameof(ShowsStageScreen))]
    private FactSheetViewModel? _factSheet;

    public bool ShowsFactSheet => FactSheet is not null;

    /// <summary>The References screen, while it is on screen in place of the matrix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsReferences), nameof(ShowsMatrix), nameof(ShowsStageScreen))]
    private ReferencesViewModel? _references;

    public bool ShowsReferences => References is not null;

    /// <summary>The Assembly screen (board 10), while it is on screen in place of the matrix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAssembly), nameof(ShowsMatrix), nameof(ShowsStageScreen))]
    private AssemblyViewModel? _assembly;

    public bool ShowsAssembly => Assembly is not null;

    /// <summary>The whole script (board 2c), while it is on screen in place of the matrix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMatrix), nameof(ShowsStageScreen))]
    private bool _showsWholeScript;

    /// <summary>A stage's own screen is on, with "← Result matrix" to go back.</summary>
    public bool ShowsStageScreen => ShowsFactSheet || ShowsReferences || ShowsAssembly || ShowsWholeScript;

    public bool ShowsMatrix => HasProject && FactSheet is null && References is null && Assembly is null && !ShowsWholeScript;

    /// <summary>The script of the open project: the matrix's rows and the panel of the selected segment.</summary>
    [ObservableProperty]
    private ScriptMatrixViewModel? _script;

    /// <summary>The script's first load after a project opened; tests await it.</summary>
    public Task ScriptLoading { get; private set; } = Task.CompletedTask;

    /// <summary>What reacting to the last engine update started; tests await it.</summary>
    public Task Updating { get; private set; } = Task.CompletedTask;

    public void Show(Project project)
    {
        LoadError = null;
        Project = project;
        FactSheet = null;
        References = null;
        Assembly = null;
        ShowsWholeScript = false;
        Breadcrumb = $"Projects / {project.Setup.Name}";
        Script?.Playback.Stop();   // the project before keeps no file open
        Script = new ScriptMatrixViewModel(_client, project.Id, _newPlayer?.Invoke(), _pickPicture, _confirm);
        ScriptLoading = Script.LoadAsync();
    }

    /// <summary>Opens a project from Recent projects.</summary>
    /// <summary>Back from Profiles: a profile fixed there is offered on a failed stage's line (#75).</summary>
    public async Task RefreshAsync()
    {
        if (Script is null)
        {
            return;
        }
        ScriptLoading = Script.LoadAsync();
        await ScriptLoading;
        if (References is { } references)
        {
            await references.LoadAsync();
        }
    }

    public async Task OpenAsync(Guid projectId)
    {
        try
        {
            Show(await _client.GetProjectAsync(projectId));
        }
        catch (Exception ex)
        {
            // The project already on screen stays; the error shows above it.
            Debug.WriteLine($"Opening project {projectId} failed: {ex}");
            LoadError = $"Could not open the project: {ex.Message}";
        }
    }

    /// <summary>
    /// After "Start run": the run starts and the fact sheet opens, so you watch the research come
    /// in and review it on the same screen.
    /// </summary>
    public async Task StartRunAsync(Project project)
    {
        Show(project);
        await ShowFactSheetAsync();
        try
        {
            await _client.StartRunAsync(project.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Starting the run of {project.Id} failed: {ex}");
            LoadError = $"Could not start the run: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenStageAsync(StageChip chip)
    {
        if (chip.Stage == PipelineStage.Research)
        {
            await ShowFactSheetAsync();
        }
        else if (chip.Stage is PipelineStage.Script or PipelineStage.Voice or PipelineStage.Storyboard or PipelineStage.Stills or PipelineStage.Clips)
        {
            ShowMatrix();
        }
        else if (chip.Stage == PipelineStage.References)
        {
            await ShowReferencesAsync();
        }
        else if (chip.Stage == PipelineStage.Assembly)
        {
            await ShowAssemblyAsync();
        }
    }

    [RelayCommand]
    private void ShowMatrix()
    {
        var away = FactSheet is not null || References is not null || Assembly is not null;
        FactSheet = null;
        References = null;
        Assembly = null;
        ShowsWholeScript = false;
        if (Project is not null)
        {
            Breadcrumb = $"Projects / {Project.Setup.Name}";
        }
        if (away && Script is not null)
        {
            // What changed there (a fact sheet, an image approved) can put results here out of date.
            ScriptLoading = Script.LoadAsync();
        }
    }

    /// <summary>"Show whole script" on the home screen: every segment to read, approve or write again (board 2c).</summary>
    [RelayCommand]
    private void ShowWholeScript()
    {
        if (Project is null)
        {
            return;
        }
        StopPlayback();
        FactSheet = null;
        References = null;
        Assembly = null;
        ShowsWholeScript = true;
        Breadcrumb = $"Projects / {Project.Setup.Name} / Whole script";
    }

    /// <summary>Stops a voice playing in the matrix: it goes out of sight, and so does its Pause.</summary>
    public void StopPlayback() => Script?.Playback.Stop();

    private async Task ShowFactSheetAsync()
    {
        if (Project is null)
        {
            return;
        }
        StopPlayback();
        References = null;
        Assembly = null;
        ShowsWholeScript = false;
        var sheet = new FactSheetViewModel(_client, Project.Id, Project.Setup.ResearchSources, _openUrl);
        // "Approve and continue": back to the matrix, where the script is being written.
        sheet.Approved += (_, _) => ShowMatrix();
        FactSheet = sheet;
        Breadcrumb = $"Projects / {Project.Setup.Name} / Research";
        await sheet.LoadAsync();
    }

    /// <summary>The References screen in place of the matrix (board 9).</summary>
    [RelayCommand]
    private async Task ShowReferencesAsync()
    {
        if (Project is null)
        {
            return;
        }
        StopPlayback();
        FactSheet = null;
        Assembly = null;
        ShowsWholeScript = false;
        var screen = new ReferencesViewModel(_client, Project.Id, _openUrl, _pickPicture, _confirm);
        References = screen;
        Breadcrumb = $"Projects / {Project.Setup.Name} / References";
        await screen.LoadAsync();
    }

    /// <summary>The Assembly screen in place of the matrix (board 10).</summary>
    [RelayCommand]
    private async Task ShowAssemblyAsync()
    {
        if (Project is null)
        {
            return;
        }
        StopPlayback();
        FactSheet = null;
        References = null;
        ShowsWholeScript = false;
        var screen = new AssemblyViewModel(_client, Project.Id);
        Assembly = screen;
        Breadcrumb = $"Projects / {Project.Setup.Name} / Assembly";
        await screen.LoadAsync();
    }

    /// <summary>A yes-or-no question in a message box.</summary>
    private static bool Confirm(string question) =>
        System.Windows.MessageBox.Show(question, "StoryForge X", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    /// <summary>The file picker "Add my own picture…" (and a still's "Replace with file…") opens.</summary>
    private static string? PickPicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a picture", Filter = "Pictures (*.png, *.jpg, *.webp)|*.png;*.jpg;*.jpeg;*.webp" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async Task ReceiveAsync(StageUpdate update)
    {
        if (Project is null || update.ProjectId != Project.Id)
        {
            return;
        }
        if (update.Key is null && Project.Stages.FirstOrDefault(s => s.Stage == update.Stage)?.State != update.State)
        {
            Project = Project with { Stages = [.. Project.Stages.Select(s => s.Stage == update.Stage ? s with { State = update.State } : s)] };
        }
        if (FactSheet is not null)
        {
            await FactSheet.ReceiveAsync(update);
        }
        if (Script is not null)
        {
            await Script.ReceiveAsync(update);
        }
        if (References is not null)
        {
            await References.ReceiveAsync(update);
        }
        if (Assembly is not null)
        {
            await Assembly.ReceiveAsync(update);
        }
    }

    private static string Duration(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    private static string ChipName(PipelineStage stage) => stage switch
    {
        PipelineStage.Storyboard => "Board",
        _ => stage.ToString(),
    };
}
