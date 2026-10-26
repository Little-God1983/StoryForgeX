using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.References;

/// <summary>
/// The References screen (board 9): the list to confirm, then a card per reference grouped as
/// characters, places and objects; the image pool; the rest of the cast; the panel of the selected
/// card or pool picture (in the .Panel file); the timeline (in the .Timeline file); the stage's bars.
/// </summary>
public sealed partial class ReferencesViewModel(IStoryForgeClient client, Guid projectId, Action<string> openUrl, Func<string?> pickPicture) : ObservableObject
{
    // Counts loads and live updates; a load that finds the count moved on drops its view.
    private int _loads;
    private ReferencesView? _view;
    private bool _acting;

    public Guid ProjectId { get; } = projectId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsMaking), nameof(IsReview), nameof(IsApproved), nameof(IsFailed), nameof(ShowsListBar), nameof(ShowsGateBar), nameof(ShowsTextOnlyBar), nameof(CanPropose))]
    [NotifyCanExecuteChangedFor(nameof(ProposeCommand), nameof(MakeImagesCommand), nameof(CancelCommand), nameof(RetryCommand), nameof(ApproveRemainingCommand), nameof(ApproveTextOnlyCommand), nameof(AddReferenceCommand))]
    private StageState _state;

    public bool IsRunning => State == StageState.Running;

    /// <summary>The stage runs, or one of its images is made again: the bar shows it, with Cancel.</summary>
    public bool IsMaking => IsRunning || !ImagesIdle;

    public bool IsReview => State is StageState.NeedsReview or StageState.Stale;

    public bool IsApproved => State == StageState.Approved;

    public bool IsFailed => State == StageState.Failed;

    /// <summary>
    /// The references were never proposed: a project whose storyboard was approved before this
    /// stage existed starts it from here.
    /// </summary>
    public bool CanPropose => State == StageState.NotStarted && _view?.StoryboardApproved == true && !_acting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListPhase), nameof(ShowsListBar), nameof(ShowsGateBar))]
    private ReferencePhase _phase;

    public bool IsListPhase => Phase == ReferencePhase.List;

    /// <summary>
    /// Past the list, a reference with no image yet or an image never made: the run was cancelled
    /// or failed before it got to them.
    /// </summary>
    private bool HasMissingImages => !IsListPhase && Cards.Any(c => c.View.Images.Count == 0 || c.View.Images.Any(i => i is { Version: 0, State: not StageState.Running }));

    /// <summary>The list waits for you: check it, then make the images. Or some images were never made.</summary>
    public bool ShowsListBar => (IsListPhase || HasMissingImages) && IsReview && !TextOnly;

    public bool ShowsGateBar => !IsListPhase && !HasMissingImages && !NothingToMake && IsReview && !TextOnly;

    /// <summary>The list was confirmed empty: no one is shown often enough to need a reference image.</summary>
    private bool NothingToMake => !IsListPhase && !Cards.Any();

    public string ListBarTitle => IsListPhase ? "Check the list" : "Images missing";

    public string ListBarText => IsListPhase
        ? "Who and what is shown in 2 or more shots, or once when it carries the story. Remove, merge or add before any image is made."
        : "Some references have no image yet: the run stopped before it got to them. Make the images to finish them.";

    /// <summary>Stills from text only, or nothing on the list: the stage waits to be approved as it is.</summary>
    public bool ShowsTextOnlyBar => (TextOnly || NothingToMake) && IsReview;

    public string TextOnlyBarText => TextOnly
        ? "This project makes its stills from text only, so it needs no references. Approve, and the run goes on."
        : "Nothing on the list needs a reference image. Approve, and the run goes on.";

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
        if (update.ProjectId != ProjectId)
        {
            return;
        }
        if (update.Stage == PipelineStage.Storyboard)
        {
            // Who and what is in a shot changed, here or in the Board panel: the timeline shows it.
            if (update.Activity is null)
            {
                await LoadAsync();
            }
            return;
        }
        if (update.Stage != PipelineStage.References)
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
        FillTimeline(view);

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
        NotifyActions();
    }

    /// <summary>What the bars offer depends on the list and on whether an action is on its way.</summary>
    private void NotifyActions()
    {
        foreach (var name in new[] { nameof(IsMaking), nameof(CanPropose), nameof(ShowsListBar), nameof(ShowsGateBar), nameof(ShowsTextOnlyBar), nameof(ListBarTitle), nameof(ListBarText), nameof(TextOnlyBarText) })
        {
            OnPropertyChanged(name);
        }
        foreach (var command in new IRelayCommand[] { CancelCommand, ProposeCommand, MakeImagesCommand, AddReferenceCommand, MakeReferenceCommand, ApproveRemainingCommand, RetryCommand, ApproveTextOnlyCommand, ToggleCellCommand })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Made again from the view; the selection is kept by id, and the thumbnails come from the converter's cache.</summary>
    private void Fill(ObservableCollection<ReferenceCardViewModel> cards, IEnumerable<ReferenceView> references, IReadOnlyDictionary<string, string> labels)
    {
        cards.Clear();
        foreach (var reference in references)
        {
            cards.Add(new ReferenceCardViewModel(reference, labels, SelectCard) { IsSelected = reference.Reference.Id == _selectedCardId });
        }
    }

    // ── The bars ──

    /// <summary>No image of the stage is being made on its own: the engine takes no change meanwhile.</summary>
    private bool ImagesIdle => Cards.SelectMany(c => c.View.Images).All(i => i.State != StageState.Running);

    private bool CanMakeImages() => (IsListPhase || HasMissingImages) && !IsRunning && ImagesIdle && !_acting;

    [RelayCommand(CanExecute = nameof(CanMakeImages))]
    private Task MakeImagesAsync() => ActAsync("make the images", () => client.MakeReferenceImagesAsync(ProjectId));

    // Only once there is a list: before it is proposed there is nothing to add to.
    private bool HasList => _view is { Version: > 0 };

    private bool CanAddReference() => HasList && !IsRunning && ImagesIdle && !_acting && NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddReference))]
    private async Task AddReferenceAsync()
    {
        if (await ActAsync("add it to the list", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Add, Name: NewName.Trim(), Kind: NewKind))))
        {
            NewName = "";
        }
    }

    private bool CanMakeReference(string name) => HasList && !IsRunning && ImagesIdle && !_acting;

    [RelayCommand(CanExecute = nameof(CanMakeReference))]
    private Task MakeReferenceAsync(string name) =>
        ActAsync("make it a reference", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.MakeReference, Name: name)));

    private bool CanApproveRemaining() => ShowsGateBar && !_acting && ImagesIdle;

    [RelayCommand(CanExecute = nameof(CanApproveRemaining))]
    private Task ApproveRemainingAsync() => ActAsync("approve the references", () => client.ApproveSegmentsAsync(ProjectId, PipelineStage.References));

    [RelayCommand(CanExecute = nameof(IsMaking))]
    private Task CancelAsync() => IsRunning
        ? ActAsync("cancel the references", () => client.CancelAsync(ProjectId, PipelineStage.References))
        : ActAsync("cancel the image", async () =>
        {
            foreach (var key in Cards.SelectMany(c => c.View.Images).Where(i => i.State == StageState.Running).Select(i => i.Key).ToList())
            {
                await client.CancelSegmentAsync(ProjectId, PipelineStage.References, key);
            }
        });

    [RelayCommand(CanExecute = nameof(CanPropose))]
    private Task ProposeAsync() => ActAsync("propose the references", () => client.RegenerateAsync(ProjectId, PipelineStage.References));

    private bool CanRetry() => IsFailed && !_acting;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => IsListPhase
        ? ActAsync("make the references", () => client.RegenerateAsync(ProjectId, PipelineStage.References))
        : ActAsync("make the images", () => client.MakeReferenceImagesAsync(ProjectId));   // past the list: the approved ones stay

    private bool CanApproveTextOnly() => (TextOnly || NothingToMake) && IsReview && !_acting;

    /// <summary>Stills from text only, or nothing on the list: the stage is approved as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanApproveTextOnly))]
    private Task ApproveTextOnlyAsync() => ActAsync("approve the references", () => client.ApproveAsync(ProjectId, PipelineStage.References, _view?.Version ?? 0));

    /// <summary>
    /// Runs one action; the engine's update then reloads the screen. The reason shows when it did
    /// not go through. While it is on its way, the others wait. True when it went through, even if
    /// the reload after it did not.
    /// </summary>
    private async Task<bool> ActAsync(string what, Func<Task> action)
    {
        _acting = true;
        RefreshPanel();
        NotifyActions();
        try
        {
            await action();
            ActionError = null;
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not {what}: {ex}");
            ActionError = $"Could not {what}: {ex.Message}";
            return false;
        }
        finally
        {
            _acting = false;
            RefreshPanel();
            NotifyActions();
        }
    }
}
