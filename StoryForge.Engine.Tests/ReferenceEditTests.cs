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
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });
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
        Assert.Equal("", (string)_comfy.Workflows[runs]["1"]!["inputs"]!["image"]!);   // the workflow's own, no upload
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
    }

    [Fact]
    public async Task Nothing_on_the_list_changes_while_the_references_are_made()
    {
        _comfy.Hold = new TaskCompletionSource();
        var client = await StartAsync();
        var project = await StoryboardApprovedAsync(client, s => s with { ReviewReferenceList = false });
        await ReferencesAsync(client, project.Id, v => v.State == StageState.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChangeReferencesAsync(project.Id, new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01")));

        _comfy.Hold.SetResult();
    }
}
