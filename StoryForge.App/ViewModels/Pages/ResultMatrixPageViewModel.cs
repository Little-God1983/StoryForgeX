using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Pages;

/// <summary>One stage chip in the project header, e.g. "Stills" with its state.</summary>
public sealed record StageChip(PipelineStage Stage, string Name, StageState State)
{
    public string StateText => State switch
    {
        StageState.NotStarted => "not started",
        _ => State.ToString(),
    };

    // What screen readers announce.
    public override string ToString() => $"{Name}: {StateText}";
}

/// <summary>
/// The Result matrix screen for the open project: its header (name, aspect, resolution, target
/// length, stage chips) and the matrix of shots by stage, empty until the run fills it (#5 on).
/// </summary>
public sealed partial class ResultMatrixPageViewModel(IStoryForgeClient client)
    : PageViewModel("Result matrix", "Projects / Result matrix", "No project open. Start one from New project.")
{
    private const string BaseBreadcrumb = "Projects / Result matrix";

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

    /// <summary>Why the last project could not be opened; null when all is well.</summary>
    [ObservableProperty]
    private string? _loadError;

    public void Show(Project project)
    {
        LoadError = null;
        Project = project;
        Breadcrumb = $"Projects / {project.Setup.Name}";
    }

    /// <summary>Opens a project from Recent projects.</summary>
    public async Task OpenAsync(Guid projectId)
    {
        try
        {
            Show(await client.GetProjectAsync(projectId));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Opening project {projectId} failed: {ex}");
            Project = null;
            Breadcrumb = BaseBreadcrumb;
            LoadError = $"Could not open the project: {ex.Message}";
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
