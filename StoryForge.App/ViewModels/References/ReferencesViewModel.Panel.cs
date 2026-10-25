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
        foreach (var name in new[]
        {
            nameof(SelectedCard), nameof(SelectedPicture), nameof(ShowsCard), nameof(ShowsPicture), nameof(ShowsNothingSelected), nameof(SelectedName), nameof(SelectedStatus),
            nameof(PreviewPath), nameof(ShowsPartial), nameof(MadeUpText), nameof(ShowsMissing), nameof(MissingWhy), nameof(ImageChoices),
            nameof(Versions), nameof(MadeFrom), nameof(Look), nameof(SelectedShots), nameof(MergeChoices), nameof(UseForChoices),
        })
        {
            OnPropertyChanged(name);
        }
        foreach (var command in new IRelayCommand[]
        {
            ApproveCommand, RegenerateCommand, ShowVersionCommand, AddImageCommand, SearchAgainCommand, AddOwnPictureCommand,
            FromDescriptionCommand, StartEditLookCommand, MergeCommand, RemoveCommand, UsePictureCommand, OpenPageCommand, DiscardPictureCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    // ── The selected reference ──

    /// <summary>Changes to a reference wait while anything of the stage is made.</summary>
    private bool CardIdle() => SelectedCard is not null && !IsRunning && Cards.SelectMany(c => c.View.Images).All(i => i.State != StageState.Running);

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
        return ActAsync("make the image again", () => client.RegenerateSegmentAsync(ProjectId, PipelineStage.References, key));
    }

    private bool CanShowVersion(int version) => CardIdle() && SelectedImage is not null;

    [RelayCommand(CanExecute = nameof(CanShowVersion))]
    private Task ShowVersionAsync(int version)
    {
        var key = SelectedImage!.Key;
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
        return ActAsync("search the sources again", () => client.SearchSourcesAgainAsync(ProjectId, id));
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private async Task AddOwnPictureAsync()
    {
        var id = SelectedCard!.Id;
        if (pickPicture() is { } path)
        {
            await ActAsync("add your picture", () => client.AddOwnPictureAsync(ProjectId, id, path));
        }
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private Task FromDescriptionAsync()
    {
        var id = SelectedCard!.Id;
        return ActAsync("make it from the description", () => client.MakeFromDescriptionAsync(ProjectId, id));
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private void StartEditLook()
    {
        EditLook = Look;
        IsEditingLook = true;
    }

    [RelayCommand(CanExecute = nameof(IsEditingLook))]
    private async Task SaveLookAsync()
    {
        var id = SelectedCard!.Id;
        await ActAsync("save the look", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: id, Look: EditLook.Trim())));
        if (ActionError is null)
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
        return ActAsync("merge the references", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: id, OtherId: into)));
    }

    [RelayCommand(CanExecute = nameof(CardIdle))]
    private Task RemoveAsync()
    {
        var id = SelectedCard!.Id;
        return ActAsync("remove it from the list", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: id)));
    }

    // ── The selected pool picture ──

    private bool PictureIdle() => SelectedPicture is not null && !IsRunning;

    private bool CanUsePicture() => PictureIdle() && UseFor is not null;

    [RelayCommand(CanExecute = nameof(CanUsePicture))]
    private Task UsePictureAsync()
    {
        var (picture, reference) = (SelectedPicture!.Id, UseFor!.Id);
        return ActAsync("use the picture", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: reference, PoolImageId: picture)));
    }

    private bool CanOpenPage() => SelectedPicture is { View.Image.Page.Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanOpenPage))]
    private void OpenPage() => openUrl(SelectedPicture!.View.Image.Page);

    [RelayCommand(CanExecute = nameof(PictureIdle))]
    private Task DiscardPictureAsync()
    {
        var picture = SelectedPicture!.Id;
        return ActAsync("discard the picture", () => client.ChangeReferencesAsync(ProjectId, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: picture)));
    }
}
