using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.References;

/// <summary>
/// The References screen (board 9): the list to confirm, then a card per reference grouped as
/// characters, places and objects; the image pool; the rest of the cast; the panel of the selected
/// card or pool picture (in the .Panel file); the stage's bars.
/// </summary>
public sealed partial class ReferencesViewModel(IStoryForgeClient client, Guid projectId, Action<string> openUrl, Func<string?> pickPicture) : ObservableObject
{
    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;
    private ReferencesView? _view;

    public Guid ProjectId { get; } = projectId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(ShowsListBar), nameof(ShowsGateBar), nameof(ShowsTextOnlyBar), nameof(CanPropose))]
    [NotifyCanExecuteChangedFor(nameof(ProposeCommand), nameof(MakeImagesCommand), nameof(CancelCommand), nameof(RetryCommand), nameof(ApproveRemainingCommand), nameof(ApproveTextOnlyCommand), nameof(AddReferenceCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    /// <summary>
    /// The references were never proposed: a project whose storyboard was approved before this
    /// stage existed starts it from here.
    /// </summary>
    public bool CanPropose => State == StageState.NotStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListPhase), nameof(ShowsListBar), nameof(ShowsGateBar))]
    private ReferencePhase _phase;

    public bool IsListPhase => Phase == ReferencePhase.List;

    /// <summary>The list waits for you: check it, then make the images.</summary>
    public bool ShowsListBar => IsListPhase && IsReview && !TextOnly;

    public bool ShowsGateBar => !IsListPhase && IsReview && !TextOnly;

    /// <summary>Stills from text only: the stage waits to be approved as it is.</summary>
    public bool ShowsTextOnlyBar => TextOnly && IsReview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsListBar), nameof(ShowsGateBar), nameof(ShowsTextOnlyBar))]
    private bool _textOnly;

    [ObservableProperty]
    private string? _error;

    /// <summary>Why the last thing you did did not work; null when it did.</summary>
    [ObservableProperty]
    private string? _actionError;

    public ObservableCollection<ActivityLine> Activity { get; } = [];

    /// <summary>"Karlach image 1: sent to ComfyUI".</summary>
    [ObservableProperty]
    private string _progress = "";

    /// <summary>"6 references → 9 images".</summary>
    [ObservableProperty]
    private string _costText = "";

    /// <summary>"2 of 6 images approved."</summary>
    [ObservableProperty]
    private string _approvedSummary = "";

    /// <summary>"12 from 6 pages".</summary>
    [ObservableProperty]
    private string _poolSummary = "";

    public ObservableCollection<ReferenceCardViewModel> Characters { get; } = [];

    public ObservableCollection<ReferenceCardViewModel> Places { get; } = [];

    public ObservableCollection<ReferenceCardViewModel> Objects { get; } = [];

    public ObservableCollection<PoolPictureViewModel> Pool { get; } = [];

    public ObservableCollection<OtherCastItem> Others { get; } = [];

    private IEnumerable<ReferenceCardViewModel> Cards => Characters.Concat(Places).Concat(Objects);

    // ── Adding to the list ──

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReferenceCommand))]
    private string _newName = "";

    [ObservableProperty]
    private CastKind _newKind = CastKind.Character;

    public static IReadOnlyList<CastKind> Kinds { get; } = Enum.GetValues<CastKind>();

    public async Task LoadAsync()
    {
        var mine = ++_loads;
        try
        {
            var view = await client.GetReferencesAsync(ProjectId);
            if (mine != _loads)
            {
                return;   // a newer load or a live update came in meanwhile; this view is old
            }
            Apply(view);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the references failed: {ex}");
            ActionError = $"Could not load the references: {ex.Message}";
        }
    }

    /// <summary>A change from the engine for this project, already on the UI thread.</summary>
    public async Task ReceiveAsync(StageUpdate update)
    {
        if (update.ProjectId != ProjectId || update.Stage != PipelineStage.References)
        {
            return;
        }
        if (update.Key is null && update.State == StageState.Running)
        {
            var starting = State != StageState.Running;
            if (starting)
            {
                _loads++;   // a load still on its way would show the state from before this run
                Activity.Clear();
                Progress = "";
                State = StageState.Running;
                Error = null;
            }
            if (update.Activity is { } line)
            {
                Activity.Add(line);
                Progress = line.Text;
            }
            if (starting)
            {
                await LoadAsync();
            }
            return;
        }
        if (update.Key is not null && update.Activity is { } imageLine)
        {
            Progress = imageLine.Text;   // one image made again: its lines show in the bar
            return;
        }
        await LoadAsync();
    }

    private void Apply(ReferencesView view)
    {
        _view = view;
        var keepLive = State == StageState.Running && view.State == StageState.Running && view.Activity.Count < Activity.Count;
        State = view.State;
        Error = view.Error;
        Phase = view.Phase;
        TextOnly = view.TextOnly;
        if (!keepLive)
        {
            Activity.Clear();
            foreach (var line in view.Activity)
            {
                Activity.Add(line);
            }
            Progress = view.Activity.Count > 0 ? view.Activity[^1].Text : "";
        }

        Fill(Characters, view.References.Where(r => r.Reference.Kind == CastKind.Character), view.ShotLabels);
        Fill(Places, view.References.Where(r => r.Reference.Kind == CastKind.Place), view.ShotLabels);
        Fill(Objects, view.References.Where(r => r.Reference.Kind == CastKind.Object), view.ShotLabels);

        Pool.Clear();
        foreach (var picture in view.Pool)
        {
            Pool.Add(new PoolPictureViewModel(picture, SelectPicture) { IsSelected = picture.Image.Id == _selectedPictureId });
        }
        Others.Clear();
        foreach (var other in view.Others)
        {
            Others.Add(new OtherCastItem(other.Name, $"{other.Name} · {(other.Shots == 0 ? "in no shot" : other.Shots == 1 ? "1 shot" : $"{other.Shots} shots")}, not key"));
        }

        var images = view.References.Sum(r => Math.Max(1, r.Images.Count));
        CostText = view.References.Count == 0 ? "" : $"{view.References.Count} references → {images} images";
        var made = view.References.SelectMany(r => r.Images).ToList();
        ApprovedSummary = $"{made.Count(i => i.State == StageState.Approved)} of {made.Count} images approved.";
        var pages = view.Pool.Select(p => p.Image.Page).Where(p => p.Length > 0).Distinct().Count();
        PoolSummary = $"{view.Pool.Count} from {pages} {(pages == 1 ? "page" : "pages")}";
        RefreshPanel();
        ApproveRemainingCommand.NotifyCanExecuteChanged();
        MakeImagesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Cards that are still there keep their place and selection; the rest come and go.</summary>
    private void Fill(ObservableCollection<ReferenceCardViewModel> cards, IEnumerable<ReferenceView> references, IReadOnlyDictionary<string, string> labels)
    {
        cards.Clear();
        foreach (var reference in references)
        {
            cards.Add(new ReferenceCardViewModel(reference, labels, SelectCard) { IsSelected = reference.Reference.Id == _selectedCardId });
        }
    }

    // ── The bars ──

    private bool CanMakeImages() => IsListPhase && !IsRunning && Cards.Any();

    [RelayCommand(CanExecute = nameof(CanMakeImages))]
    private Task MakeImagesAsync() => ActAsync("make the images", () => client.MakeReferenceImagesAsync(ProjectId));

    private bool CanAddReference() => !IsRunning && NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddReference))]
    private async Task AddReferenceAsync()
    {
        await ActAsync("add it to the list", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Add, Name: NewName.Trim(), Kind: NewKind)));
        if (ActionError is null)
        {
            NewName = "";
        }
    }

    [RelayCommand]
    private Task MakeReferenceAsync(string name) =>
        ActAsync("make it a reference", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.MakeReference, Name: name)));

    private bool CanApproveRemaining() => ShowsGateBar && Cards.SelectMany(c => c.View.Images).All(i => i.State != StageState.Running);

    [RelayCommand(CanExecute = nameof(CanApproveRemaining))]
    private Task ApproveRemainingAsync() => ActAsync("approve the references", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.References));

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task CancelAsync() => ActAsync("cancel the references", () => client.CancelAsync(ProjectId, PipelineStage.References));

    [RelayCommand(CanExecute = nameof(CanPropose))]
    private Task ProposeAsync() => ActAsync("propose the references", () => client.RegenerateAsync(ProjectId, PipelineStage.References));

    [RelayCommand(CanExecute = nameof(IsFailed))]
    private Task RetryAsync() => ActAsync("make the references", () => client.RegenerateAsync(ProjectId, PipelineStage.References));

    private bool CanApproveTextOnly() => TextOnly && IsReview;

    /// <summary>Stills from text only: the stage is approved as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanApproveTextOnly))]
    private Task ApproveTextOnlyAsync() => ActAsync("approve the references", () => client.ApproveAsync(ProjectId, PipelineStage.References, _view?.Version ?? 0));

    /// <summary>Runs one action; the engine's update then reloads the screen. The reason shows when it did not go through.</summary>
    private async Task ActAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            ActionError = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not {what}: {ex}");
            ActionError = $"Could not {what}: {ex.Message}";
        }
    }
}
