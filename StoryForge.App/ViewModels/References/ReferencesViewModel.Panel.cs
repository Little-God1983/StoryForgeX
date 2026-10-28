using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.App.ViewModels.Script;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.References;

/// <summary>
/// The panel on the right: the selected reference (its image, how it came about, the three ways
/// when nothing could be made, what it is made from, its look and shots, and what you do with it),
/// or the selected pool picture (where it is from, what it is used for, use it, discard it).
/// </summary>
public sealed partial class ReferencesViewModel
{
    private string? _selectedCardId;
    private string? _selectedPictureId;
    private int _selectedNumber = 1;

    public ReferenceCardViewModel? SelectedCard => Cards.FirstOrDefault(c => c.Id == _selectedCardId);

    public PoolPictureViewModel? SelectedPicture => Pool.FirstOrDefault(p => p.Id == _selectedPictureId);

    public bool ShowsCard => SelectedCard is not null;

    public bool ShowsPicture => SelectedPicture is not null && SelectedCard is null;

    public bool ShowsNothingSelected => !ShowsCard && !ShowsPicture;

    /// <summary>The image the panel shows: the one picked from the card's images, else the first.</summary>
    private ReferenceImageView? SelectedImage =>
        SelectedCard?.View.Images.FirstOrDefault(i => i.Number == _selectedNumber) ?? SelectedCard?.View.Images.FirstOrDefault();

    public string SelectedName => SelectedCard?.Name ?? "";

    /// <summary>"review · image 1 · version 2".</summary>
    public string SelectedStatus => SelectedImage is { } image
        ? $"{ReferenceCardViewModel.Word(image)} · image {image.Number}{(image.Version > 0 ? $" · version {image.Version}" : "")}"
        : SelectedCard?.StateText ?? "";

    public string? PreviewPath => SelectedImage?.Path;

    public bool ShowsPartial => SelectedImage is { Image.Made: ImageMaking.PartlyMadeUp };

    /// <summary>"Partly made up: legs, shoes".</summary>
    public string MadeUpText => ShowsPartial ? $"Partly made up: {SelectedImage!.Image.Missing}" : "";

    public bool ShowsMissing => SelectedImage is { Version: > 0, Image.Made: ImageMaking.NoPicture };

    /// <summary>The image is your picture, used as it is: no model made it.</summary>
    public bool ShowsAsIs => SelectedImage is { Version: > 0, Image.Made: ImageMaking.AsIs };

    /// <summary>Why nothing was made, e.g. "No picture was found on the sources."</summary>
    public string MissingWhy => ShowsMissing ? SelectedImage!.Image.Missing : "";

    public IReadOnlyList<ReferenceImageChoice> ImageChoices =>
        SelectedCard is { } card ? [.. card.View.Images.Select(i => new ReferenceImageChoice(i, i.Number == SelectedImage?.Number))] : [];

    public IReadOnlyList<SegmentVersionChoice> Versions => SelectedImage is { } image
        ? [.. image.Versions.Select(v => new SegmentVersionChoice(v.Version, v.Version == image.Version, v.Version == image.ApprovedVersion))]
        : [];

    /// <summary>The captions of the pool pictures the reference is made from.</summary>
    public IReadOnlyList<string> MadeFrom => SelectedCard is { } card
        ? [.. card.View.Reference.Sources.Select(id => Pool.FirstOrDefault(p => p.Id == id)?.Caption ?? id)]
        : [];

    public string Look => SelectedCard?.View.Reference.Look ?? "";

    public IReadOnlyList<string> SelectedShots => SelectedCard?.Shots ?? [];

    /// <summary>The other references, to merge the selected one into.</summary>
    public IReadOnlyList<ReferenceChoice> MergeChoices => [.. Cards.Where(c => c.Id != _selectedCardId).Select(c => new ReferenceChoice(c.Id, c.Name))];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private ReferenceChoice? _mergeTarget;

    /// <summary>Every reference, to use the selected pool picture for.</summary>
    public IReadOnlyList<ReferenceChoice> UseForChoices => [.. Cards.Select(c => new ReferenceChoice(c.Id, c.Name))];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UsePictureCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAsItIsCommand))]
    private ReferenceChoice? _useFor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditingLook))]
    [NotifyCanExecuteChangedFor(nameof(SaveLookCommand))]
    private bool _isEditingLook;

    public bool IsNotEditingLook => !IsEditingLook;

    [ObservableProperty]
    private string _editLook = "";

    private void SelectCard(ReferenceCardViewModel card)
    {
        _selectedCardId = card.Id;
        _selectedPictureId = null;
        _selectedNumber = card.View.Images.FirstOrDefault()?.Number ?? 1;
        IsEditingLook = false;
        MergeTarget = null;
        RefreshPanel();
    }

    private void SelectPicture(PoolPictureViewModel picture)
    {
        _selectedPictureId = picture.Id;
        _selectedCardId = null;
        UseFor = null;
        IsEditingLook = false;
        RefreshPanel();
    }

    [RelayCommand]
    private void SelectImage(int number)
    {
        _selectedNumber = number;
        RefreshPanel();
    }

    /// <summary>The selection or what it shows changed: the panel and the cards read it again.</summary>
    private void RefreshPanel()
    {
        foreach (var card in Cards)
        {
            card.IsSelected = card.Id == _selectedCardId;
        }
        foreach (var picture in Pool)
        {
            picture.IsSelected = picture.Id == _selectedPictureId && _selectedCardId is null;
        }
        RefreshTimeline();
        foreach (var name in new[]
        {
            nameof(SelectedCard), nameof(SelectedPicture), nameof(ShowsCard), nameof(ShowsPicture), nameof(ShowsNothingSelected), nameof(SelectedName), nameof(SelectedStatus),
            nameof(PreviewPath), nameof(ShowsPartial), nameof(MadeUpText), nameof(ShowsMissing), nameof(MissingWhy), nameof(ShowsAsIs), nameof(ImageChoices),
            nameof(Versions), nameof(MadeFrom), nameof(Look), nameof(SelectedShots), nameof(MergeChoices), nameof(UseForChoices), nameof(RerunText),
        })
        {
            OnPropertyChanged(name);
        }
        foreach (var command in new IRelayCommand[]
        {
            ApproveCommand, RegenerateCommand, ShowVersionCommand, AddImageCommand, SearchAgainCommand, AddOwnPictureCommand,
            FromDescriptionCommand, StartEditLookCommand, SaveLookCommand, MergeCommand, RemoveCommand, RemoveImageCommand, UsePictureCommand, UseAsItIsCommand, OpenPageCommand, DiscardPictureCommand,
            RerunFromHereCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    // ── The selected reference ──

    /// <summary>
    /// Before a change to references that stills are made from: "6 stills use Karlach and would be
    /// out of date. Go on?". True when none does, or you said yes.
    /// </summary>
    /// <param name="all">The change reaches every still showing them (a look, a merge, a removal), not only the ones their images went into.</param>
    private bool Confirmed(IReadOnlyList<string> referenceIds, bool all = false)
    {
        var counts = all ? _view?.StillsShowing : _view?.StillsUsing;
        return Ask([.. referenceIds.Select(id => (id, counts?.GetValueOrDefault(id) ?? 0))]);
    }

    private bool Confirmed(string referenceId, bool all = false) => Confirmed([referenceId], all);

    /// <summary>Before a change to one image: only the stills that image went into count.</summary>
    private bool ConfirmedImage(string imageKey) =>
        Ask([(ReferencesStage(imageKey), _view?.StillsUsingImage.GetValueOrDefault(imageKey) ?? 0)]);

    private static string ReferencesStage(string imageKey) => imageKey.Split('/')[0];

    private bool Ask(IReadOnlyList<(string Id, int Count)> reached)
    {
        var count = reached.Sum(r => r.Count);
        if (count == 0 || confirm is null || _view is null)
        {
            return true;
        }
        var names = string.Join(" and ", reached.Where(r => r.Count > 0)
            .Select(r => _view.References.FirstOrDefault(v => v.Reference.Id == r.Id)?.Reference.Name ?? r.Id));
        return confirm($"{count} {(count == 1 ? "still uses" : "stills use")} {names} and would be out of date. Go on?");
    }

    /// <summary>Changes to a reference wait while anything of the stage is made, or another change is on its way.</summary>
    private bool CardIdle() => SelectedCard is not null && !IsRunning && !_acting && ImagesIdle;

    private bool CanApprove() => CardIdle() && SelectedImage is { Version: > 0, State: not StageState.Approved } image && image.Image.Made != ImageMaking.NoPicture;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private Task ApproveAsync()
    {
        var image = SelectedImage!;
        return ActAsync("approve the image", () => client.ApproveSegmentAsync(ProjectId, PipelineStage.References, image.Key, image.Version));
    }

    private bool CanRegenerate() => CardIdle() && SelectedImage is { Version: > 0 };

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync()
    {
        var key = SelectedImage!.Key;
        return ConfirmedImage(key) ? ActAsync("make the image again", () => client.RegenerateSegmentAsync(ProjectId, PipelineStage.References, key)) : Task.CompletedTask;
    }

    /// <summary>A rerun waits on the image: "Part of a rerun: once you approve it, the stills that use it are made again."; null for none.</summary>
    public string? RerunText => SelectedImage is { Rerun: true } image
        ? $"Part of a rerun: {(image.State == StageState.Approved ? "the stills that use it are made again next." : "once you approve it, the stills that use it are made again.")}"
        : null;

    // Only an image some still went into: from any other, nothing would be made again.
    private bool CanRerunFromHere() => CardIdle() && !IsListPhase && SelectedImage is { Version: > 0 } image && _view?.StillsUsingImage.GetValueOrDefault(image.Key) > 0;

    /// <summary>Rerun pipeline from here ↓: the stills that use the image made again. Asks first with how many.</summary>
    [RelayCommand(CanExecute = nameof(CanRerunFromHere))]
    private async Task RerunFromHereAsync()
    {
        var key = SelectedImage!.Key;
        string plan;
        try
        {
            plan = await client.GetRerunPlanAsync(ProjectId, PipelineStage.References, key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not rerun the stills from here: {ex}");
            ActionError = $"Could not rerun the stills from here: {ex.Message}";
            return;
        }
        if (confirm?.Invoke($"{plan} Go on?") != false)
        {
            await ActAsync("rerun the stills from here", () => client.RerunFromHereAsync(ProjectId, PipelineStage.References, key));
        }
    }

    [RelayCommand]
    private Task StopRerunAsync() => ActAsync("stop the rerun", () => client.StopRerunAsync(ProjectId));

    private bool CanShowVersion(int version) => CardIdle() && SelectedImage is not null;

    [RelayCommand(CanExecute = nameof(CanShowVersion))]
    private Task ShowVersionAsync(int version)
    {
        var key = SelectedImage!.Key;
        // The stills are made from the approved version, which stays: nothing to ask.
        return ActAsync("switch the version", () => client.SelectSegmentVersionAsync(ProjectId, PipelineStage.References, key, version));
    }

    private bool CanAddImage() => CardIdle() && !IsListPhase;

    [RelayCommand(CanExecute = nameof(CanAddImage))]
    private Task AddImageAsync()
    {
        var id = SelectedCard!.Id;
        return ActAsync("make one more image", () => client.AddReferenceImageAsync(ProjectId, id));
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private Task SearchAgainAsync()
    {
        var id = SelectedCard!.Id;
        return Confirmed(id) ? ActAsync("search the sources again", () => client.SearchSourcesAgainAsync(ProjectId, id)) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private async Task AddOwnPictureAsync()
    {
        var id = SelectedCard!.Id;
        if (Confirmed(id) && pickPicture() is { } path)
        {
            await ActAsync("add your picture", () => client.AddOwnPictureAsync(ProjectId, id, path));
        }
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private Task FromDescriptionAsync()
    {
        var id = SelectedCard!.Id;
        return Confirmed(id) ? ActAsync("make it from the description", () => client.MakeFromDescriptionAsync(ProjectId, id)) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private void StartEditLook()
    {
        EditLook = Look;
        IsEditingLook = true;
    }

    // Your text stays while the references are made; it is saved once they are done.
    private bool CanSaveLook() => IsEditingLook && SelectedCard is not null && !IsRunning && ImagesIdle && !_acting;

    [RelayCommand(CanExecute = nameof(CanSaveLook))]
    private async Task SaveLookAsync()
    {
        var id = SelectedCard!.Id;
        if (Confirmed(id, all: true) && await ActAsync("save the look", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: id, Look: EditLook.Trim()))))
        {
            IsEditingLook = false;   // saved; when it was not, your text stays to try again
        }
    }

    [RelayCommand]
    private void CancelLook() => IsEditingLook = false;

    private bool CanMerge() => CardIdle() && MergeTarget is not null;

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeAsync()
    {
        var (id, into) = (SelectedCard!.Id, MergeTarget!.Id);
        return Confirmed([id, into], all: true) ? ActAsync("merge the references", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: id, OtherId: into))) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private Task RemoveAsync()
    {
        var id = SelectedCard!.Id;
        return Confirmed(id, all: true) ? ActAsync("remove it from the list", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: id))) : Task.CompletedTask;
    }

    /// <summary>One of several images: e.g. one that came out with nothing on it. The last one stays.</summary>
    private bool CanRemoveImage() => CardIdle() && SelectedImage is not null && SelectedCard!.View.Images.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemoveImage))]
    private Task RemoveImageAsync()
    {
        var key = SelectedImage!.Key;
        return ConfirmedImage(key) ? ActAsync("remove the image", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.RemoveImage, ImageKey: key))) : Task.CompletedTask;
    }

    // ── The selected pool picture ──

    private bool PictureIdle() => SelectedPicture is not null && !IsRunning && ImagesIdle && !_acting;

    // One too small to make an image from would be passed over.
    private bool CanUsePicture() => PictureIdle() && UseFor is not null && !SelectedPicture!.TooSmall;

    [RelayCommand(CanExecute = nameof(CanUsePicture))]
    private Task UsePictureAsync()
    {
        var (picture, reference) = (SelectedPicture!.Id, UseFor!.Id);
        return Confirmed(reference) ? ActAsync("use the picture", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: reference, PoolImageId: picture))) : Task.CompletedTask;
    }

    // An image it can be exists once the list is confirmed.
    private bool CanUseAsItIs() => CanUsePicture() && !IsListPhase;

    [RelayCommand(CanExecute = nameof(CanUseAsItIs))]
    private Task UseAsItIsAsync()
    {
        var (picture, reference) = (SelectedPicture!.Id, UseFor!.Id);
        return Confirmed(reference) ? ActAsync("use the picture as it is", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.UseAsItIs, ReferenceId: reference, PoolImageId: picture))) : Task.CompletedTask;
    }

    private bool CanOpenPage() => SelectedPicture is { View.Image.Page.Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanOpenPage))]
    private void OpenPage() => openUrl(SelectedPicture!.View.Image.Page);

    [RelayCommand(CanExecute = nameof(PictureIdle))]
    private Task DiscardPictureAsync()
    {
        var picture = SelectedPicture!.Id;
        // The references made from it are made from the others then: their stills go out of date.
        var made = _view?.References.Where(r => r.Reference.Sources.Contains(picture)).Select(r => r.Reference.Id).ToList() ?? [];
        return !Confirmed(made) ? Task.CompletedTask : ActAsync("discard the picture", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: picture)));
    }
}
