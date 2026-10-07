using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Research;
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

    /// <summary>Only stages with a screen of their own open one; the others arrive with their issues.</summary>
    public bool CanOpen => Stage == PipelineStage.Research;

    // What screen readers announce.
    public override string ToString() => $"{Name}: {StateText}";
}

/// <summary>
/// The Result matrix screen for the open project: its header (name, aspect, resolution, target
/// length, stage chips) and the matrix of shots by stage, empty until the script fills it (#6 on).
/// The Research chip opens the fact sheet in the same place.
/// </summary>
public sealed partial class ResultMatrixPageViewModel : PageViewModel
{
    private readonly IStoryForgeClient _client;
    private readonly Action<string> _openUrl;

    /// <param name="openUrl">Opens a source page in the browser; tests pass their own.</param>
    public ResultMatrixPageViewModel(IStoryForgeClient client, Action<string>? openUrl = null)
        : base("Result matrix", "Projects / Result matrix", "No project open. Start one from New project.")
    {
        _client = client;
        _openUrl = openUrl ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        client.StageUpdated += (_, update) => UiThread.Run(() => Updating = ReceiveAsync(update));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Name), nameof(Summary), nameof(Stages))]
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
    [NotifyPropertyChangedFor(nameof(ShowsFactSheet), nameof(ShowsMatrix))]
    private FactSheetViewModel? _factSheet;

    public bool ShowsFactSheet => FactSheet is not null;

    public bool ShowsMatrix => HasProject && FactSheet is null;

    /// <summary>What reacting to the last engine update started; tests await it.</summary>
    public Task Updating { get; private set; } = Task.CompletedTask;

    public void Show(Project project)
    {
        LoadError = null;
        Project = project;
        FactSheet = null;
        Breadcrumb = $"Projects / {project.Setup.Name}";
    }

    /// <summary>Opens a project from Recent projects.</summary>
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
        if (chip.CanOpen)
        {
            await ShowFactSheetAsync();
        }
    }

    [RelayCommand]
    private void ShowMatrix()
    {
        FactSheet = null;
        if (Project is not null)
        {
            Breadcrumb = $"Projects / {Project.Setup.Name}";
        }
    }

    private async Task ShowFactSheetAsync()
    {
        if (Project is null)
        {
            return;
        }
        var sheet = new FactSheetViewModel(_client, Project.Id, Project.Setup.ResearchSources, _openUrl);
        FactSheet = sheet;
        Breadcrumb = $"Projects / {Project.Setup.Name} / Research";
        await sheet.LoadAsync();
    }

    private async Task ReceiveAsync(StageUpdate update)
    {
        if (Project is null || update.ProjectId != Project.Id)
        {
            return;
        }
        if (Project.Stages.FirstOrDefault(s => s.Stage == update.Stage)?.State != update.State)
        {
            Project = Project with { Stages = [.. Project.Stages.Select(s => s.Stage == update.Stage ? s with { State = update.State } : s)] };
        }
        if (FactSheet is not null)
        {
            await FactSheet.ReceiveAsync(update);
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
