using StoryForge.Client;
using StoryForge.Engine.References;

namespace StoryForge.Engine.Tests;

/// <summary>
/// What you change on the References screen: the list, the pictures a reference is made from, your
/// own pictures, searching again, making from the description, one more image.
/// </summary>
public sealed partial class ReferencesRunTests
{
    /// <summary>A PNG file's start: enough for its size to be read.</summary>
    internal static byte[] Png(int width, int height)
    {
        byte[] head = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        byte[] size = [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height];
        return [.. head, .. size, 8, 6, 0, 0, 0];
    }

    private async Task<(IStoryForgeClient Client, Guid ProjectId)> ListedAsync()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client);
        await ReferencesAsync(client, project.Id, v => v.State == StageState.NeedsReview);
        return (client, project.Id);
    }

    private async Task<(IStoryForgeClient Client, Guid ProjectId)> ImagedAsync()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });
        await ReferencesAsync(client, project.Id, ImagesMade);
        return (client, project.Id);
    }

    [Fact]
    public void The_size_of_a_PNG_JPEG_or_WebP_is_read_from_its_header()
    {
        Assert.Equal((600, 400), ImageSize.Of(Png(600, 400)));
        Assert.Equal((640, 480), ImageSize.Of([0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 1, 224, 2, 128, 3]));
        Assert.Null(ImageSize.Of([1, 2, 3, 4]));
    }

    [Fact]
    public void A_JPEG_padded_with_fill_bytes_before_a_marker_is_read_too()
    {
        Assert.Equal((640, 480), ImageSize.Of([0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xFF, 0xFF, 0xC0, 0, 11, 8, 1, 224, 2, 128, 3]));
    }

    [Fact]
    public void The_blank_canvas_is_a_light_square_picture()
    {
        var png = BlankCanvas.Png(1024);

        Assert.Equal((1024, 1024), ImageSize.Of(png));
        Assert.Equal(".png", ImageSize.Ending(png));
    }

    [Fact]
    public async Task A_name_added_to_the_list_shows_with_the_shots_it_is_in()
    {
        var (client, projectId) = await ListedAsync();

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Add, Name: "Raphael", Kind: CastKind.Character));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Equal(("R03", "Raphael", ReferencePhase.List), (view.References[2].Reference.Id, view.References[2].Reference.Name, view.Phase));
        Assert.Empty(view.References[2].Images);
    }

    [Fact]
    public async Task A_name_from_the_rest_of_the_cast_becomes_a_reference()
    {
        var (client, projectId) = await ListedAsync();

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.MakeReference, Name: "Mammon's mint"));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Equal((CastKind.Place, "Where every coin is made."), (view.References[2].Reference.Kind, view.References[2].Reference.KeyReason));
        Assert.Empty(view.Others);
    }

    [Fact]
    public async Task Removing_a_reference_takes_its_images_and_the_rest_can_be_approved()
    {
        var (client, projectId) = await ImagedAsync();

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R02"));
        await client.ApproveSegmentsAsync(projectId, PipelineStage.References);

        var view = await ReferencesAsync(client, projectId, v => v.State == StageState.Approved);
        Assert.Equal(["Karlach"], view.References.Select(r => r.Reference.Name));
        Assert.Equal(StageState.Approved, view.State);
    }

    [Fact]
    public async Task A_reference_removed_after_the_stage_was_approved_leaves_the_approved_list_too()
    {
        var (client, projectId) = await ImagedAsync();
        await client.ApproveSegmentsAsync(projectId, PipelineStage.References);
        await ReferencesAsync(client, projectId, v => v.State == StageState.Approved);

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R02"));

        await using var db = await _engine.DbAsync();
        var whole = db.Cells.Single(c => c.ProjectId == projectId && c.Stage == PipelineStage.References && c.Key == "");
        Assert.Equal((StageState.Approved, whole.CurrentVersion), (whole.State, whole.ApprovedVersion));
    }

    [Fact]
    public async Task Merging_two_references_keeps_one_with_the_shots_and_pictures_of_both()
    {
        var (client, projectId) = await ImagedAsync();
        var before = await client.GetReferencesAsync(projectId);

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));

        var view = await client.GetReferencesAsync(projectId);
        var merged = Assert.Single(view.References).Reference;
        Assert.Equal(before.References.SelectMany(r => r.Reference.Shots).Distinct().Order(), merged.Shots.Order());
        Assert.Equal([.. before.References[0].Reference.Sources, .. before.References[1].Reference.Sources], merged.Sources);
    }

    [Fact]
    public async Task The_look_and_the_pictures_a_reference_is_made_from_can_be_changed()
    {
        var (client, projectId) = await ListedAsync();
        var coinPicture = (await client.GetReferencesAsync(projectId)).References[1].Reference.Sources[0];

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: "R01", Look: "Red skin, horns, an infernal engine in her chest."));
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R01", PoolImageId: coinPicture));
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DropSource, ReferenceId: "R02", PoolImageId: coinPicture));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Equal("Red skin, horns, an infernal engine in her chest.", view.References[0].Reference.Look);
        Assert.Contains(coinPicture, view.References[0].Reference.Sources);
        Assert.Empty(view.References[1].Reference.Sources);
        Assert.Equal(["Karlach"], view.Pool.Single(p => p.Image.Id == coinPicture).UsedFor);
    }

    [Fact]
    public async Task A_picture_discarded_from_the_pool_leaves_every_reference()
    {
        var (client, projectId) = await ListedAsync();
        var picture = (await client.GetReferencesAsync(projectId)).Pool[0];

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: picture.Image.Id));

        var view = await client.GetReferencesAsync(projectId);
        Assert.DoesNotContain(view.Pool, p => p.Image.Id == picture.Image.Id);
        Assert.All(view.References, r => Assert.DoesNotContain(picture.Image.Id, r.Reference.Sources));
        Assert.False(File.Exists(picture.Path));
    }

    [Fact]
    public async Task Your_own_picture_goes_into_the_pool_and_the_image_is_made_from_it()
    {
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();
        Assert.Equal(ImageMaking.NoPicture, (await client.GetReferencesAsync(projectId)).References[1].Images[0].Image.Made);
        var own = Path.Combine(_engine.DataDirectory, "coin artwork.png");
        await File.WriteAllBytesAsync(own, Png(900, 900));

        await client.AddOwnPictureAsync(projectId, "R02", own);
        var view = await ReferencesAsync(client, projectId, v => v.References[1].Images[0].Version == 2);

        var picture = view.Pool.Single(p => p.Image.Origin == PoolOrigin.Own).Image;
        Assert.Equal(("coin artwork.png", 900, 900, ""), (picture.Url, picture.Width, picture.Height, picture.Page));
        Assert.Equal(picture.Id, view.References[1].Reference.Sources[0]);
        Assert.Equal(ImageMaking.PartlyMadeUp, view.References[1].Images[0].Image.Made);   // Claude said "the back" is missing
    }

    [Fact]
    public async Task Your_own_picture_is_used_even_when_Claude_does_not_recognise_it()
    {
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();
        var own = Path.Combine(_engine.DataDirectory, "coin fan art.png");
        await File.WriteAllBytesAsync(own, Png(900, 900));
        _check.ByName["Soul coin"] = new PictureVerdict(false, false, "", "A gold coin with a horned skull.");

        await client.AddOwnPictureAsync(projectId, "R02", own);
        var view = await ReferencesAsync(client, projectId, v => v.References[1].Images[0] is { Version: 2, State: not StageState.Running });

        var picture = view.Pool.Single(p => p.Image.Origin == PoolOrigin.Own).Image;
        Assert.Equal(ImageMaking.FromPicture, view.References[1].Images[0].Image.Made);
        Assert.Equal([picture.Id], view.References[1].Images[0].Image.Sources);
        Assert.Equal("A gold coin with a horned skull.", view.References[1].Reference.Look);   // what the image was made from shows
    }

    [Fact]
    public async Task A_picture_you_picked_is_used_even_when_Claude_does_not_recognise_it()
    {
        var (client, projectId) = await ImagedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png").Image.Id;
        _check.ByName["Karlach"] = new PictureVerdict(false, false, "", "");

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R01", PoolImageId: coin));
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images[0] is { Version: 2, State: not StageState.Running });

        Assert.Equal(ImageMaking.FromPicture, view.References[0].Images[0].Image.Made);
        Assert.Equal([coin], view.References[0].Images[0].Image.Sources);
    }

    [Fact]
    public async Task Searching_the_sources_again_finds_what_is_there_now()
    {
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Coin2.png", page: "https://bg3.wiki/wiki/Soul_Coin")];

        await client.SearchSourcesAgainAsync(projectId, "R02");
        var view = await ReferencesAsync(client, projectId, v => v.References[1].Images[0].Version == 2);

        Assert.NotEqual(ImageMaking.NoPicture, view.References[1].Images[0].Image.Made);
        Assert.Single(view.References[1].Reference.Sources);
    }

    [Fact]
    public async Task Made_from_the_description_it_uses_no_picture_at_all()
    {
        var (client, projectId) = await ImagedAsync();
        var runs = _comfy.Workflows.Count;

        await client.MakeFromDescriptionAsync(projectId, "R02");
        var view = await ReferencesAsync(client, projectId, v => v.References[1].Images[0].Version == 2);

        Assert.Equal(ImageMaking.FromDescription, view.References[1].Images[0].Image.Made);
        // A blank canvas, not the picture the workflow was saved with.
        Assert.Equal("uploaded-blank.png", (string)_comfy.Workflows[runs]["1"]!["inputs"]!["image"]!);
        Assert.Contains("A tarnished iron coin.", (string)_comfy.Workflows[runs]["2"]!["inputs"]!["prompt"]!);
    }

    [Fact]
    public async Task With_no_look_there_is_nothing_to_make_it_from()
    {
        var (client, projectId) = await ListedAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.MakeFromDescriptionAsync(projectId, "R01"));

        Assert.Equal("Write how Karlach looks first (Edit look…): that is all it is made from.", refused.Message);
    }

    [Fact]
    public async Task One_more_image_of_a_reference_is_made_as_its_next_number()
    {
        var (client, projectId) = await ImagedAsync();

        await client.AddReferenceImageAsync(projectId, "R01");
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images.Count == 2 && v.References[0].Images[1].State != StageState.Running);

        Assert.Equal(("R01/2", 2, StageState.NeedsReview), (view.References[0].Images[1].Key, view.References[0].Images[1].Number, view.References[0].Images[1].State));
    }

    [Fact]
    public async Task A_reference_added_after_the_images_gets_its_pictures_and_an_image()
    {
        var (client, projectId) = await ImagedAsync();
        _pictures.ByName["Raphael"] = [FakePictures.Picture("Raphael.png", page: "https://bg3.wiki/wiki/Raphael")];

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Add, Name: "Raphael", Kind: CastKind.Character));
        var view = await ReferencesAsync(client, projectId, v => v.References.Count == 3 && v.References[2].Images.Count == 1 && v.References[2].Images[0].State != StageState.Running);

        Assert.Equal(ImageMaking.FromPicture, view.References[2].Images[0].Image.Made);
        Assert.Single(view.References[2].Reference.Sources);
        Assert.Equal("Raphael as the picture shows.", view.References[2].Reference.Look);   // kept for its next images
    }

    [Fact]
    public async Task Nothing_on_the_list_changes_while_the_references_are_made()
    {
        _comfy.Hold = new TaskCompletionSource();
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });
        await ReferencesAsync(client, project.Id, v => v.State == StageState.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01")));

        _comfy.Hold.SetResult();
    }

    [Fact]
    public async Task The_references_are_not_approved_while_one_of_them_has_no_image()
    {
        // The list confirmed, the run fails at the soul coin, after Karlach's image is stored.
        var (client, projectId) = await ListedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        _comfy.FailFor = "Soul coin";
        await client.MakeReferenceImagesAsync(projectId);
        var failed = await ReferencesAsync(client, projectId, v => v.State == StageState.Failed);
        var karlach = failed.References[0].Images.Single();

        await client.ApproveSegmentAsync(projectId, PipelineStage.References, karlach.Key, karlach.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentsAsync(projectId, PipelineStage.References));

        Assert.NotEqual(StageState.Approved, (await client.GetReferencesAsync(projectId)).State);
    }

    [Fact]
    public async Task An_image_whose_first_make_failed_is_not_approved()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images.Count == 2 && v.References[0].Images[1].State == StageState.Failed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentsAsync(projectId, PipelineStage.References));
    }

    [Fact]
    public async Task A_name_added_while_the_list_is_checked_is_searched_at_once()
    {
        var (client, projectId) = await ListedAsync();
        _pictures.ByName["Raphael"] = [FakePictures.Picture("Raphael.png", page: "https://bg3.wiki/wiki/Raphael")];

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Add, Name: "Raphael", Kind: CastKind.Character));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Single(view.References[2].Reference.Sources);
        Assert.Empty(view.References[2].Images);   // the list is still being checked: nothing is made yet
    }

    [Fact]
    public async Task References_are_not_proposed_from_a_storyboard_still_in_review()
    {
        var client = await StartAsync();
        _board.Answer(StoryboardRunTests.Good());
        _script.Answer(ScriptRunTests.Good());
        var project = await ScriptRunTests.ApprovedFactsAsync(client, s => s with { Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Script or PipelineStage.Voice))] });
        await ScriptRunTests.WaitForAsync(() => client.GetStoryboardAsync(project.Id), v => v.State == StageState.NeedsReview);
        Assert.False((await client.GetReferencesAsync(project.Id)).StoryboardApproved);

        await client.RegenerateAsync(project.Id, PipelineStage.References);

        var view = await ReferencesAsync(client, project.Id, _ => false);
        Assert.Equal("Approve the storyboard first: the references are proposed from it.", view.Error);
    }

    [Fact]
    public async Task Two_searches_at_once_keep_both_and_give_their_pictures_ids_of_their_own()
    {
        var (client, projectId) = await ListedAsync();
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach2.png")];
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Coin2.png", page: "https://bg3.wiki/wiki/Soul_Coin")];
        _pictures.Delay = TimeSpan.FromMilliseconds(200);

        await Task.WhenAll(client.SearchSourcesAgainAsync(projectId, "R01"), client.SearchSourcesAgainAsync(projectId, "R02"));

        var view = await client.GetReferencesAsync(projectId);
        Assert.Equal(view.Pool.Count, view.Pool.Select(p => p.Image.Id).Distinct().Count());
        Assert.Contains(view.Pool, p => p.Image.Caption == "Karlach2.png" && view.References[0].Reference.Sources.Contains(p.Image.Id));
        Assert.Contains(view.Pool, p => p.Image.Caption == "Coin2.png" && view.References[1].Reference.Sources.Contains(p.Image.Id));
    }

    [Fact]
    public async Task Making_the_missing_images_keeps_the_ones_you_approved()
    {
        // The run fails at the soul coin after Karlach's image is stored; you approve Karlach's.
        var (client, projectId) = await ListedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        _comfy.FailFor = "Soul coin";
        await client.MakeReferenceImagesAsync(projectId);
        var failed = await ReferencesAsync(client, projectId, v => v.State == StageState.Failed);
        var karlach = failed.References[0].Images.Single();
        await client.ApproveSegmentAsync(projectId, PipelineStage.References, karlach.Key, karlach.Version);
        _comfy.Failure = null;
        var runs = _comfy.Workflows.Count;

        await client.MakeReferenceImagesAsync(projectId);
        var view = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[1].Images is [{ Version: > 0, State: not StageState.Running }]);

        Assert.Equal((karlach.Version, StageState.Approved), (view.References[0].Images[0].Version, view.References[0].Images[0].State));
        Assert.Equal(runs + 1, _comfy.Workflows.Count);   // the soul coin's image only
        Assert.Equal((StageState.NeedsReview, null), (view.State, view.Error));   // the failure is over: back to review
    }

    [Fact]
    public async Task A_list_with_nothing_on_it_lets_the_stills_go_ahead()
    {
        var (client, projectId) = await ListedAsync();
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01"));
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R02"));

        await client.MakeReferenceImagesAsync(projectId);
        var view = await ReferencesAsync(client, projectId, v => v.Phase == ReferencePhase.Images && v.State == StageState.NeedsReview);
        await client.ApproveAsync(projectId, PipelineStage.References, view.Version);

        Assert.Equal(StageState.Approved, (await client.GetReferencesAsync(projectId)).State);
    }

    [Fact]
    public async Task A_picture_you_discarded_is_not_found_again()
    {
        var (client, projectId) = await ListedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png").Image;
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: coin.Id));
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Coin.png", page: "https://bg3.wiki/wiki/Soul_Coin"), FakePictures.Picture("Coin2.png", page: "https://bg3.wiki/wiki/Soul_Coin")];

        await client.SearchSourcesAgainAsync(projectId, "R02");

        var view = await client.GetReferencesAsync(projectId);
        Assert.DoesNotContain(view.Pool, p => p.Image.Url == coin.Url);
        var found = view.Pool.Single(p => p.Image.Caption == "Coin2.png").Image;
        Assert.NotEqual(coin.Id, found.Id);   // a new id: images made from the old one still name it
        Assert.Equal([found.Id], view.References[1].Reference.Sources);
    }

    [Fact]
    public async Task With_every_image_there_making_the_images_only_brings_a_failed_stage_back_to_review()
    {
        var (client, projectId) = await ListedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        _comfy.FailFor = "Soul coin";
        await client.MakeReferenceImagesAsync(projectId);
        await ReferencesAsync(client, projectId, v => v.State == StageState.Failed);
        _comfy.Failure = null;
        await client.MakeReferenceImagesAsync(projectId);
        var made = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[1].Images is [{ Version: > 0, State: not StageState.Running }]);
        var karlach = made.References[0].Images.Single();
        await client.ApproveSegmentAsync(projectId, PipelineStage.References, karlach.Key, karlach.Version);
        var runs = _comfy.Workflows.Count;

        await client.MakeReferenceImagesAsync(projectId);

        var view = await client.GetReferencesAsync(projectId);
        Assert.NotEqual(StageState.Running, view.State);   // no run of the whole stage
        Assert.Equal(runs, _comfy.Workflows.Count);
        Assert.Equal(StageState.Approved, view.References[0].Images[0].State);
    }

    [Fact]
    public async Task A_change_that_finds_the_references_being_made_when_it_is_saved_is_refused()
    {
        var (client, projectId) = await ImagedAsync();
        _pictures.ByName["Raphael"] = [FakePictures.Picture("Raphael.png", page: "https://bg3.wiki/wiki/Raphael")];
        _pictures.Delay = TimeSpan.FromMilliseconds(500);
        _comfy.Hold = new TaskCompletionSource();

        var adding = client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Add, Name: "Raphael", Kind: CastKind.Character));
        await Task.Delay(100);
        await client.RegenerateAsync(projectId, PipelineStage.References);   // the whole stage, while Raphael is searched

        await Assert.ThrowsAsync<InvalidOperationException>(() => adding);

        Assert.Equal(2, (await client.GetReferencesAsync(projectId)).References.Count);   // not saved beside the run
        _comfy.Hold.SetResult();
    }

    [Fact]
    public async Task An_image_whose_first_make_failed_is_made_with_the_missing_ones()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images.Count == 2 && v.References[0].Images[1].State == StageState.Failed);
        _comfy.Failure = null;

        await client.MakeReferenceImagesAsync(projectId);
        var view = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[0].Images[1] is { Version: > 0, State: not StageState.Running });

        Assert.Equal(StageState.NeedsReview, view.References[0].Images[1].State);
    }

    [Fact]
    public async Task An_empty_list_needs_nothing_to_make_images_with()
    {
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { Provider = "Elsewhere" } });
        await ReferencesAsync(client, project.Id, v => v.State == StageState.NeedsReview);
        await client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01"));
        await client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R02"));

        await client.MakeReferenceImagesAsync(project.Id);
        var view = await ReferencesAsync(client, project.Id, v => v.Phase == ReferencePhase.Images && v.State == StageState.NeedsReview);

        Assert.Null(view.Error);
    }

    [Fact]
    public async Task One_more_image_needs_a_picture_to_make_it_from()
    {
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AddReferenceImageAsync(projectId, "R02"));

        Assert.Equal("There is no picture to make another image of Soul coin from. Search the sources again, add your own picture, or make it from the description.", refused.Message);
    }

    [Fact]
    public async Task An_image_that_shows_nothing_can_be_removed_and_the_rest_approved()
    {
        var (client, projectId) = await ImagedAsync();
        var view = await client.GetReferencesAsync(projectId);
        foreach (var image in view.References.SelectMany(r => r.Images))
        {
            await client.ApproveSegmentAsync(projectId, PipelineStage.References, image.Key, image.Version);
        }
        _check.Decide = request => new PictureVerdict(false, false, "", "");   // no picture shows Karlach any more
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { Version: > 0, State: not StageState.Running }]);

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.RemoveImage, ImageKey: "R01/2"));

        var after = await client.GetReferencesAsync(projectId);
        Assert.Single(after.References[0].Images);
        Assert.Equal(StageState.Approved, after.State);
    }

    [Fact]
    public async Task The_only_image_of_a_reference_is_not_removed()
    {
        var (client, projectId) = await ImagedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.RemoveImage, ImageKey: "R02/1")));
    }

    [Fact]
    public async Task The_stage_is_not_approved_as_a_whole_while_its_images_wait()
    {
        var (client, projectId) = await ImagedAsync();
        var view = await client.GetReferencesAsync(projectId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveAsync(projectId, PipelineStage.References, view.Version));

        Assert.NotEqual(StageState.Approved, (await client.GetReferencesAsync(projectId)).State);
    }

    [Fact]
    public async Task A_look_is_not_emptied_while_it_is_all_the_reference_is_made_from()
    {
        var (client, projectId) = await ImagedAsync();
        await client.MakeFromDescriptionAsync(projectId, "R02");
        await ReferencesAsync(client, projectId, v => v.References[1].Images[0] is { Version: 2, State: not StageState.Running });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: "R02", Look: " ")));
    }

    [Fact]
    public async Task A_removed_reference_goes_back_to_the_rest_of_the_cast()
    {
        var (client, projectId) = await ListedAsync();

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01"));
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.MakeReference, Name: "Karlach"));

        Assert.Contains((await client.GetReferencesAsync(projectId)).References, r => r.Reference.Name == "Karlach");
    }

    [Fact]
    public async Task A_run_that_fails_partway_keeps_the_list_it_proposed()
    {
        _comfy.Failure = "ComfyUI is not reachable.";
        _comfy.FailFor = "Soul coin";
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });

        var failed = await ReferencesAsync(client, project.Id, v => v.State == StageState.Failed);

        Assert.Equal(2, failed.References.Count);
        Assert.Single(failed.References[0].Images);
        _comfy.Failure = null;
        var runs = _comfy.Workflows.Count;
        await client.MakeReferenceImagesAsync(project.Id);
        await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(project.Id), v => v.References[1].Images is [{ Version: > 0, State: not StageState.Running }]);
        Assert.Equal(runs + 1, _comfy.Workflows.Count);   // the soul coin only, no new list
    }

    [Fact]
    public async Task A_picture_you_picked_stays_when_the_sources_are_searched_again()
    {
        var (client, projectId) = await ListedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png").Image.Id;
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R01", PoolImageId: coin));
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach2.png")];

        await client.SearchSourcesAgainAsync(projectId, "R01");

        Assert.Contains(coin, (await client.GetReferencesAsync(projectId)).References[0].Reference.Sources);
    }

    [Fact]
    public async Task A_picture_you_picked_is_what_the_image_is_made_from()
    {
        var (client, projectId) = await ImagedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png").Image.Id;

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R01", PoolImageId: coin));
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images[0] is { Version: 2, State: not StageState.Running });

        Assert.Equal(coin, view.References[0].Reference.Sources[0]);
        Assert.Equal([coin], view.References[0].Images[0].Image.Sources);
    }

    [Fact]
    public async Task References_with_no_usable_picture_need_no_image_workflow()
    {
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Tiny.png", 200, 200)];
        _pictures.ByName["Soul coin"] = [FakePictures.Picture("Tiny coin.png", 200, 200, page: "https://bg3.wiki/wiki/Soul_Coin")];
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false, Provider = "Elsewhere" } });

        var view = await ReferencesAsync(client, project.Id, ImagesMade);

        Assert.Null(view.Error);
        Assert.All(view.References, r => Assert.Equal(ImageMaking.NoPicture, r.Images.Single().Image.Made));
    }

    [Fact]
    public async Task An_image_profile_that_does_not_say_where_the_picture_goes_is_refused()
    {
        // At the start: the project could never make its references (the stage checks it too, for projects saved before).
        _inputs = [.. FakeImageComfy.Inputs.Where(i => i.Key != "image")];
        var client = await StartAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } }));

        Assert.Contains("'image'", refused.Message);
        Assert.Empty(_comfy.Workflows);
    }

    [Fact]
    public async Task A_run_that_fails_partway_keeps_the_look_its_images_were_made_with()
    {
        _comfy.Failure = "ComfyUI is not reachable.";
        _comfy.FailFor = "Soul coin";
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });

        var failed = await ReferencesAsync(client, project.Id, v => v.State == StageState.Failed);

        Assert.Equal("Karlach as the picture shows.", failed.References[0].Reference.Look);
    }

    [Fact]
    public async Task A_reference_whose_only_image_was_never_made_gets_that_one_made_not_another()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { State: StageState.Failed }]);
        _comfy.Failure = null;
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.RemoveImage, ImageKey: "R01/1"));

        await client.MakeReferenceImagesAsync(projectId);
        var view = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[0].Images.All(i => i is { Version: > 0, State: not StageState.Running }));

        Assert.Equal(["R01/2"], view.References[0].Images.Select(i => i.Key));
    }

    [Fact]
    public async Task Cancelling_part_way_keeps_the_images_made_and_says_Retry_makes_the_rest()
    {
        _comfy.Hold = new TaskCompletionSource();
        _comfy.HoldFor = "Soul coin";
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });
        await ReferencesAsync(client, project.Id, v => v.References is [{ Images: [{ Version: > 0 }] }, ..]);

        await client.CancelAsync(project.Id, PipelineStage.References);
        var view = await ReferencesAsync(client, project.Id, _ => false);

        Assert.Equal("Cancelled after 1 image. Those are kept; Retry makes the ones still missing.", view.Error);
    }

    [Fact]
    public async Task A_workflow_that_saves_its_picture_with_a_capital_ending_is_read()
    {
        _comfy.Saved = "ComfyUI_00001_.PNG";
        var (client, projectId) = await ImagedAsync();

        var view = await client.GetReferencesAsync(projectId);

        Assert.Null(view.Error);
        Assert.All(view.References, r => Assert.NotNull(r.Images[0].Path));
    }

    [Fact]
    public async Task Searching_again_when_the_sources_find_nothing_keeps_what_the_reference_has()
    {
        var (client, projectId) = await ImagedAsync();
        var before = await client.GetReferencesAsync(projectId);
        var runs = _comfy.Workflows.Count;
        _pictures.ByName["Karlach"] = [];   // the wiki cannot be reached just now

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchSourcesAgainAsync(projectId, "R01"));

        var after = await client.GetReferencesAsync(projectId);
        Assert.Equal(before.References[0].Reference.Sources, after.References[0].Reference.Sources);
        Assert.Equal(before.References[0].Images[0].Version, after.References[0].Images[0].Version);
        Assert.Equal(runs, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task A_removed_reference_takes_its_image_files_with_it()
    {
        var (client, projectId) = await ImagedAsync();
        var file = (await client.GetReferencesAsync(projectId)).References[1].Images[0].Path!;
        Assert.True(File.Exists(file));

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R02"));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Your_own_picture_too_small_to_make_from_is_refused()
    {
        var (client, projectId) = await ImagedAsync();
        var own = Path.Combine(_engine.DataDirectory, "tiny coin.png");
        await File.WriteAllBytesAsync(own, Png(300, 300));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.AddOwnPictureAsync(projectId, "R02", own));

        Assert.Contains("512", refused.Message);
        Assert.DoesNotContain((await client.GetReferencesAsync(projectId)).Pool, p => p.Image.Origin == PoolOrigin.Own);
    }

    [Fact]
    public async Task A_new_picture_makes_the_images_never_made_too()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { State: StageState.Failed }]);
        _comfy.Failure = null;
        var own = Path.Combine(_engine.DataDirectory, "karlach artwork.png");
        await File.WriteAllBytesAsync(own, Png(900, 900));

        await client.AddOwnPictureAsync(projectId, "R01", own);
        var view = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[0].Images.All(i => i is { Version: > 0, State: not StageState.Running }));

        Assert.Equal(["R01/1", "R01/2"], view.References[0].Images.Select(i => i.Key));
    }

    [Fact]
    public async Task A_picture_too_small_to_make_from_is_not_used()
    {
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Karlach.png"), FakePictures.Picture("Tiny.png", 200, 200, main: false)];
        var (client, projectId) = await ListedAsync();
        var tiny = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.TooSmall).Image.Id;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R02", PoolImageId: tiny)));
    }

    [Fact]
    public async Task Approving_the_rest_while_an_image_was_never_made_says_to_make_the_images()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { State: StageState.Failed }]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentsAsync(projectId, PipelineStage.References));

        Assert.Equal("1 image was never made: make the images first.", refused.Message);
    }

    [Fact]
    public async Task Cancelling_before_the_first_image_keeps_the_list_and_says_Retry_makes_what_is_missing()
    {
        _comfy.Hold = new TaskCompletionSource();
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { References = s.References with { ReviewList = false } });
        await ReferencesAsync(client, project.Id, v => v is { State: StageState.Running, Phase: ReferencePhase.Images, References.Count: 2 });

        await client.CancelAsync(project.Id, PipelineStage.References);
        var view = await ReferencesAsync(client, project.Id, _ => false);

        Assert.Equal("Cancelled before any image was made. The list is kept; Retry makes the ones still missing.", view.Error);
        Assert.Equal(2, view.References.Count);
    }

    [Fact]
    public async Task A_picture_whose_file_is_gone_is_passed_over_not_the_end_of_the_run()
    {
        var (client, projectId) = await ImagedAsync();
        var before = await client.GetReferencesAsync(projectId);
        File.Delete(before.Pool.Single(p => p.Image.Id == before.References[0].Reference.Sources[0]).Path!);

        await client.RegenerateSegmentAsync(projectId, PipelineStage.References, "R01/1");
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images[0] is { Version: 2, State: not StageState.Running });

        Assert.Null(view.References[0].Images[0].Error);
        Assert.Equal(ImageMaking.NoPicture, view.References[0].Images[0].Image.Made);
        Assert.Contains("gone", view.References[0].Images[0].Image.Missing);
    }

    [Fact]
    public async Task Approving_the_rest_with_an_extra_image_that_shows_nothing_says_to_remove_it()
    {
        var (client, projectId) = await ImagedAsync();
        _check.Decide = request => new PictureVerdict(false, false, "", "");   // no picture shows Karlach any more
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { Version: > 0, State: not StageState.Running }]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveSegmentsAsync(projectId, PipelineStage.References));

        Assert.Equal("Karlach has an image that shows nothing: remove it (Remove this image), then approve the rest.", refused.Message);
    }

    [Fact]
    public async Task A_discarded_picture_whose_file_is_still_open_is_discarded_all_the_same()
    {
        var (client, projectId) = await ListedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png");

        using (new FileStream(coin.Path!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: coin.Image.Id));
        }

        Assert.DoesNotContain((await client.GetReferencesAsync(projectId)).Pool, p => p.Image.Id == coin.Image.Id);
    }

    [Fact]
    public async Task Merging_into_a_reference_that_shows_nothing_makes_it_from_the_pictures_it_gets()
    {
        _pictures.ByName["Karlach"] = [];
        var (client, projectId) = await ImagedAsync();
        Assert.Equal(ImageMaking.NoPicture, (await client.GetReferencesAsync(projectId)).References[0].Images[0].Image.Made);

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images[0] is { Version: 2, State: not StageState.Running });

        var coin = view.Pool.Single(p => p.Image.Caption == "Coin.png").Image.Id;
        Assert.NotEqual(ImageMaking.NoPicture, view.References[0].Images[0].Image.Made);
        Assert.Equal([coin], view.References[0].Images[0].Image.Sources);
    }

    [Fact]
    public async Task Merging_into_a_reference_with_an_image_keeps_that_image()
    {
        var (client, projectId) = await ImagedAsync();
        var runs = _comfy.Workflows.Count;

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));

        // A remake would be queued, and the image running, by now.
        var image = (await client.GetReferencesAsync(projectId)).References[0].Images[0];
        Assert.Equal((1, StageState.NeedsReview), (image.Version, image.State));
        Assert.Equal(runs, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task Merging_makes_only_the_images_that_show_nothing_again()
    {
        var (client, projectId) = await ImagedAsync();
        _check.Decide = request => request.Name == "Karlach" ? new PictureVerdict(false, false, "", "") : null;
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { Version: > 0, State: not StageState.Running }]);
        _check.Decide = null;

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { Version: 2, State: not StageState.Running }]);

        Assert.Equal(1, view.References[0].Images[0].Version);
        Assert.NotEqual(ImageMaking.NoPicture, view.References[0].Images[1].Image.Made);
    }

    [Fact]
    public async Task Merging_in_nothing_to_make_from_makes_nothing_again()
    {
        _pictures.ByName["Karlach"] = [];
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();
        var runs = _comfy.Workflows.Count;

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));

        var image = (await client.GetReferencesAsync(projectId)).References[0].Images[0];
        Assert.Equal((1, StageState.NeedsReview), (image.Version, image.State));
        Assert.Equal(runs, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task Merging_in_a_reference_made_from_its_look_makes_the_one_with_nothing_from_that_look()
    {
        _pictures.ByName["Karlach"] = [];
        _pictures.ByName["Soul coin"] = [];
        var (client, projectId) = await ImagedAsync();
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: "R02", Look: "A tarnished iron coin."));
        await client.MakeFromDescriptionAsync(projectId, "R02");
        await ReferencesAsync(client, projectId, v => v.References[1].Images[0] is { Version: 2, State: not StageState.Running });

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images[0] is { Version: 2, State: not StageState.Running });

        Assert.True(view.References[0].Reference.DescriptionOnly);
        Assert.Equal(ImageMaking.FromDescription, view.References[0].Images[0].Image.Made);
    }

    [Fact]
    public async Task A_new_image_cancelled_before_it_was_made_is_gone_and_the_stage_stays_approved()
    {
        var (client, projectId) = await ImagedAsync();
        await client.ApproveSegmentsAsync(projectId, PipelineStage.References);
        await ReferencesAsync(client, projectId, v => v.State == StageState.Approved);
        _comfy.Hold = new TaskCompletionSource();
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { State: StageState.Running }]);

        await client.CancelSegmentAsync(projectId, PipelineStage.References, "R01/2");
        var view = await ReferencesAsync(client, projectId, v => v.References[0].Images.Count == 1);

        Assert.Equal(StageState.Approved, view.State);
    }

    [Fact]
    public async Task Searching_again_that_finds_the_same_pictures_keeps_the_images_as_they_are()
    {
        var (client, projectId) = await ImagedAsync();
        var before = await client.GetReferencesAsync(projectId);
        var runs = _comfy.Workflows.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchSourcesAgainAsync(projectId, "R01"));

        var after = await client.GetReferencesAsync(projectId);
        Assert.Equal((before.References[0].Images[0].Version, StageState.NeedsReview), (after.References[0].Images[0].Version, after.References[0].Images[0].State));
        Assert.Equal(runs, _comfy.Workflows.Count);
    }

    [Fact]
    public async Task A_reference_added_to_an_approved_stage_takes_the_approval_back_even_when_its_image_is_cancelled()
    {
        var (client, projectId) = await ImagedAsync();
        await client.ApproveSegmentsAsync(projectId, PipelineStage.References);
        await ReferencesAsync(client, projectId, v => v.State == StageState.Approved);
        _pictures.ByName["Gale"] = [FakePictures.Picture("Gale.png", page: "https://bg3.wiki/wiki/Gale")];
        _comfy.Hold = new TaskCompletionSource();
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Add, Name: "Gale", Kind: CastKind.Character));
        await ReferencesAsync(client, projectId, v => v.References[2].Images is [{ State: StageState.Running }]);

        await client.CancelSegmentAsync(projectId, PipelineStage.References, "R03/1");
        var view = await ReferencesAsync(client, projectId, v => v.References[2].Images.Count == 0);

        Assert.Equal(StageState.NeedsReview, view.State);
    }

    [Fact]
    public async Task A_picture_you_took_off_a_reference_does_not_come_back_when_its_sources_are_searched_again()
    {
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Wyll.png"), FakePictures.Picture("Karlach.png", main: false)];
        var (client, projectId) = await ListedAsync();
        var wyll = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Wyll.png").Image.Id;
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DropSource, ReferenceId: "R01", PoolImageId: wyll));
        _pictures.ByName["Karlach"] = [FakePictures.Picture("Wyll.png"), FakePictures.Picture("Karlach.png", main: false), FakePictures.Picture("Karlach2.png", main: false)];

        await client.SearchSourcesAgainAsync(projectId, "R01");

        Assert.DoesNotContain(wyll, (await client.GetReferencesAsync(projectId)).References[0].Reference.Sources);
    }

    [Fact]
    public async Task One_more_image_is_a_new_one_even_when_the_last_was_never_made()
    {
        var (client, projectId) = await ImagedAsync();
        _comfy.Failure = "ComfyUI is not reachable.";
        await client.AddReferenceImageAsync(projectId, "R01");
        await ReferencesAsync(client, projectId, v => v.References[0].Images is [_, { State: StageState.Failed }]);
        _comfy.Failure = null;

        await client.AddReferenceImageAsync(projectId, "R01");
        var view = await ScriptRunTests.WaitForAsync(() => client.GetReferencesAsync(projectId), v => v.References[0].Images is [_, _, { Version: > 0, State: not StageState.Running }]);

        Assert.Equal(["R01/1", "R01/2", "R01/3"], view.References[0].Images.Select(i => i.Key));
    }

    [Fact]
    public async Task A_pool_picture_is_as_big_as_the_file_that_came_not_as_the_wiki_says()
    {
        _pictures.Content["https://bg3.wiki/w/images/Karlach.png"] = Png(300, 400);   // the wiki says 800 x 1200
        var (client, projectId) = await ListedAsync();

        var picture = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Karlach.png").Image;

        Assert.Equal((300, 400, true), (picture.Width, picture.Height, picture.TooSmall));
    }

    [Fact]
    public async Task What_an_image_is_made_from_all_goes_into_its_input_hash()
    {
        var (client, projectId) = await ListedAsync();
        var setup = (await client.GetProjectAsync(projectId)).Setup;
        var karlach = (await client.GetReferencesAsync(projectId)).References[0].Reference;

        var hash = ReferencesStage.Hash(setup, karlach);

        Assert.NotEqual(hash, ReferencesStage.Hash(setup, karlach with { Chosen = ["P01"] }));
        Assert.NotEqual(hash, ReferencesStage.Hash(setup, karlach with { KeyReason = "Carries the soul coin." }));
    }

    [Fact]
    public async Task A_picture_you_took_off_a_reference_stays_off_when_another_is_merged_into_it()
    {
        var (client, projectId) = await ListedAsync();
        var coin = (await client.GetReferencesAsync(projectId)).Pool.Single(p => p.Image.Caption == "Coin.png").Image.Id;
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R01", PoolImageId: coin));
        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.DropSource, ReferenceId: "R01", PoolImageId: coin));

        await client.ChangeReferencesAsync(projectId, new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R02", OtherId: "R01"));

        var merged = (await client.GetReferencesAsync(projectId)).References.Single().Reference;
        Assert.DoesNotContain(coin, merged.Sources);
        Assert.Contains(coin, merged.Dropped ?? []);
    }
}
