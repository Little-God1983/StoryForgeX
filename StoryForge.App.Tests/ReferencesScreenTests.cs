using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.References;
using StoryForge.Client;
using static StoryForge.App.Tests.StoryboardMatrixTests;

namespace StoryForge.App.Tests;

/// <summary>The References screen (board 9): the list, the cards, the pool, the panel and what you do there.</summary>
public sealed class ReferencesScreenTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly List<string> _opened = [];
    private string? _picked;

    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string> { ["S01/a"] = "S01·1", ["S01/b"] = "S01·2", ["S02/a"] = "S02·1" };

    private static PoolImageView Picture(string id, string caption, int side = 800, PoolOrigin origin = PoolOrigin.Found, params string[] usedFor) =>
        new(new PoolImage(id, $"https://bg3.wiki/w/images/{caption}", origin == PoolOrigin.Own ? "" : "https://bg3.wiki/wiki/Karlach", caption, side, side, $"pool/{id}.png", origin),
            null, usedFor);

    private static Reference Reference(string id, string name, CastKind kind, params string[] sources) =>
        new(id, name, kind, "", name == "Karlach" ? "Red skin, horns." : "", ["S01/a", "S01/b"], sources);

    private static ReferenceImageView Image(string referenceId, ImageMaking made, StageState state = StageState.NeedsReview, string missing = "", int version = 1) =>
        new($"{referenceId}/1", 1, state, version, state == StageState.Approved ? version : null,
            [.. Enumerable.Range(1, version).Select(v => new ResultVersion(v, VersionOrigin.Generated, At, null))],
            new ReferenceImage(referenceId, 1, made == ImageMaking.NoPicture ? null : $"references/{referenceId}-1.png", [], 1, made, missing), null, null, []);

    /// <summary>Karlach made from her picture, the soul coin partly made up, the House of Hope with no usable picture.</summary>
    private static ReferencesView Images(Guid projectId, StageState state = StageState.NeedsReview) => new(
        projectId, state, null, [], ReferencePhase.Images,
        [
            new(Reference("R01", "Karlach", CastKind.Character, "P01"), [Image("R01", ImageMaking.FromPicture, StageState.Approved)]),
            new(Reference("R02", "Soul coin", CastKind.Object, "P02"), [Image("R02", ImageMaking.PartlyMadeUp, missing: "the back")]),
            new(Reference("R03", "House of Hope", CastKind.Place), [Image("R03", ImageMaking.NoPicture, missing: "No picture was found on the sources.")]),
        ],
        [Picture("P01", "Karlach.png", usedFor: "Karlach"), Picture("P02", "Coin.png", usedFor: "Soul coin"), Picture("P03", "Thumb.png", 200)],
        [new OtherCast("A tavern mug", CastKind.Object, 1)], false, Labels, 3);

    private static ReferencesView Listed(Guid projectId) => Images(projectId) with
    {
        Phase = ReferencePhase.List,
        References = [.. Images(projectId).References.Select(r => r with { Images = [] })],
    };

    private async Task<(ResultMatrixPageViewModel Page, ReferencesViewModel Screen, Project Project)> OpenAsync(Func<Guid, ReferencesView> view)
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook")], "", 10, 60, []);
        _client.References[project.Id] = view(project.Id);
        var page = new ResultMatrixPageViewModel(_client, url => _opened.Add(url), pickPicture: () => _picked);
        page.Show(project);
        await page.ScriptLoading;
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        return (page, page.References!, project);
    }

    [Fact]
    public async Task The_references_chip_opens_the_screen_and_the_link_goes_back()
    {
        var (page, screen, _) = await OpenAsync(id => Images(id));

        Assert.True(page.ShowsReferences);
        Assert.False(page.ShowsMatrix);
        Assert.EndsWith("/ References", page.Breadcrumb);
        Assert.Equal(["Karlach"], screen.Characters.Select(c => c.Name));
        Assert.Equal(["House of Hope"], screen.Places.Select(c => c.Name));
        Assert.Equal(["Soul coin"], screen.Objects.Select(c => c.Name));

        page.ShowMatrixCommand.Execute(null);
        Assert.True(page.ShowsMatrix);
        Assert.Null(page.References);
    }

    [Fact]
    public async Task On_the_list_nothing_is_made_until_you_confirm_it()
    {
        var (_, screen, _) = await OpenAsync(Listed);

        Assert.True(screen.IsListPhase);
        Assert.Equal("3 references → 3 images", screen.CostText);
        await screen.MakeImagesCommand.ExecuteAsync(null);

        Assert.Equal(("make images", ""), _client.ReferenceActions.Single());
    }

    [Fact]
    public async Task A_name_is_added_to_the_list_with_its_kind()
    {
        var (_, screen, _) = await OpenAsync(Listed);

        screen.NewName = " Raphael ";
        screen.NewKind = CastKind.Character;
        await screen.AddReferenceCommand.ExecuteAsync(null);

        Assert.Equal(new ReferenceChange(ReferenceChangeKind.Add, Name: "Raphael", Kind: CastKind.Character), _client.ReferenceChanges.Single());
        Assert.Equal("", screen.NewName);
    }

    [Fact]
    public async Task Each_card_says_how_its_image_came_about()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));

        Assert.Equal(("approved", "In 2 shots", "from 1 picture"), (screen.Characters[0].StateText, screen.Characters[0].Usage, screen.Characters[0].SourcesText));
        Assert.Equal("Partly made up: the back", screen.Objects[0].Note);
        Assert.Equal(("no usable picture", "No picture was found on the sources."), (screen.Places[0].StateText, screen.Places[0].Note));
        Assert.True(screen.Places[0].IsMissing);
        Assert.Equal("1 of 3 images approved.", screen.ApprovedSummary);
    }

    [Fact]
    public async Task A_card_without_a_usable_picture_offers_the_three_ways_and_no_approve()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Places[0].SelectCommand.Execute(null);
        _picked = @"C:\art\house of hope.png";

        Assert.True(screen.ShowsMissing);
        Assert.False(screen.ApproveCommand.CanExecute(null));
        await screen.SearchAgainCommand.ExecuteAsync(null);
        await screen.AddOwnPictureCommand.ExecuteAsync(null);
        await screen.FromDescriptionCommand.ExecuteAsync(null);

        Assert.Equal([("search again", "R03"), ("own", "R03"), ("from description", "R03")], _client.ReferenceActions);
    }

    [Fact]
    public async Task A_partly_made_up_card_asks_you_to_look_closely()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));

        screen.Objects[0].SelectCommand.Execute(null);

        Assert.True(screen.ShowsPartial);
        Assert.Equal("Partly made up: the back", screen.MadeUpText);
        Assert.Equal(["Coin.png"], screen.MadeFrom);
        Assert.Equal(["S01·1", "S01·2"], screen.SelectedShots);
        await screen.ApproveCommand.ExecuteAsync(null);
        await screen.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal([("approve v1", "R02/1"), ("regenerate", "R02/1")], _client.ShotActions);
    }

    [Fact]
    public async Task The_look_is_edited_and_a_reference_merged_or_removed()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Characters[0].SelectCommand.Execute(null);

        screen.StartEditLookCommand.Execute(null);
        Assert.Equal("Red skin, horns.", screen.EditLook);
        screen.EditLook = "Red skin, horns, an infernal engine.";
        await screen.SaveLookCommand.ExecuteAsync(null);
        screen.MergeTarget = screen.MergeChoices.Single(c => c.Id == "R02");
        await screen.MergeCommand.ExecuteAsync(null);
        await screen.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(
        [
            new ReferenceChange(ReferenceChangeKind.EditLook, ReferenceId: "R01", Look: "Red skin, horns, an infernal engine."),
            new ReferenceChange(ReferenceChangeKind.Merge, ReferenceId: "R01", OtherId: "R02"),
            new ReferenceChange(ReferenceChangeKind.Remove, ReferenceId: "R01"),
        ], _client.ReferenceChanges);
    }

    [Fact]
    public async Task A_pool_picture_is_used_for_a_reference_opened_or_discarded()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));

        screen.Pool[1].SelectCommand.Execute(null);
        screen.UseFor = screen.UseForChoices.Single(c => c.Id == "R03");
        await screen.UsePictureCommand.ExecuteAsync(null);
        screen.Pool[2].SelectCommand.Execute(null);
        screen.UseFor = screen.UseForChoices.Single(c => c.Id == "R03");

        Assert.True(screen.ShowsPicture);
        Assert.True(screen.SelectedPicture!.TooSmall);
        Assert.Equal("200 × 200 · bg3.wiki", screen.SelectedPicture.Meta);
        Assert.False(screen.UsePictureCommand.CanExecute(null));   // too small to make an image from
        screen.OpenPageCommand.Execute(null);
        await screen.DiscardPictureCommand.ExecuteAsync(null);

        Assert.Equal(
        [
            new ReferenceChange(ReferenceChangeKind.UseSource, ReferenceId: "R03", PoolImageId: "P02"),
            new ReferenceChange(ReferenceChangeKind.DiscardFromPool, PoolImageId: "P03"),
        ], _client.ReferenceChanges);
        Assert.Equal(["https://bg3.wiki/wiki/Karlach"], _opened);
    }

    [Fact]
    public async Task One_more_image_and_a_name_from_the_rest_of_the_cast()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Characters[0].SelectCommand.Execute(null);

        await screen.AddImageCommand.ExecuteAsync(null);
        await screen.MakeReferenceCommand.ExecuteAsync("A tavern mug");

        Assert.Equal(("add image", "R01"), _client.ReferenceActions.Single());
        Assert.Equal(new ReferenceChange(ReferenceChangeKind.MakeReference, Name: "A tavern mug"), _client.ReferenceChanges.Single());
        Assert.Equal(["A tavern mug · 1 shot, not key"], screen.Others.Select(o => o.Text));
    }

    [Fact]
    public async Task While_the_references_are_made_the_bar_shows_its_last_line()
    {
        var (page, screen, project) = await OpenAsync(Listed);

        _client.References[project.Id] = Listed(project.Id) with { State = StageState.Running };
        _client.Raise(new StageUpdate(project.Id, PipelineStage.References, StageState.Running, new ActivityLine(At, ActivityKind.Model, "Karlach image 1: sent to ComfyUI")));
        await page.Updating;

        Assert.True(screen.IsRunning);
        Assert.Equal("Karlach image 1: sent to ComfyUI", screen.Progress);
        await screen.CancelCommand.ExecuteAsync(null);
        Assert.Equal((project.Id, PipelineStage.References), _client.Cancelled.Single());
    }

    [Fact]
    public async Task A_failed_run_says_why_and_retries()
    {
        var (_, screen, project) = await OpenAsync(id => Listed(id) with { State = StageState.Failed, Error = "Karlach: ComfyUI is not reachable." });

        Assert.Equal("Karlach: ComfyUI is not reachable.", screen.Error);
        await screen.RetryCommand.ExecuteAsync(null);

        Assert.Equal((project.Id, PipelineStage.References), _client.Regenerated.Single());
    }

    [Fact]
    public async Task A_project_with_stills_from_text_only_approves_the_stage_as_it_is()
    {
        var (_, screen, project) = await OpenAsync(id => new ReferencesView(id, StageState.NeedsReview, null, [], ReferencePhase.Images, [], [], [], true, Labels, 2));

        Assert.True(screen.TextOnly);
        await screen.ApproveTextOnlyCommand.ExecuteAsync(null);

        Assert.Equal((project.Id, PipelineStage.References, 2), _client.Approved.Single());
    }

    [Fact]
    public async Task Approve_remaining_approves_every_image()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));

        await screen.ApproveRemainingCommand.ExecuteAsync(null);

        Assert.Equal(("approve all", ""), _client.ShotActions.Single());
    }

    [Fact]
    public async Task References_never_proposed_can_be_proposed_from_the_screen()
    {
        var (_, screen, project) = await OpenAsync(id => new ReferencesView(id, StageState.NotStarted, null, [], ReferencePhase.List, [], [], [], false, Labels, 0, StoryboardApproved: true));

        Assert.True(screen.CanPropose);
        await screen.ProposeCommand.ExecuteAsync(null);

        Assert.Equal((project.Id, PipelineStage.References), _client.Regenerated.Single());
    }

    [Fact]
    public void A_pool_picture_without_a_caption_shows_its_file_name()
    {
        var picture = new PoolImageView(new PoolImage("P01", "https://bg3.wiki/w/images/b/b1/Karlach%27s_portrait.png", "https://bg3.wiki/wiki/Karlach", "", 997, 1148, "pool/P01.png", PoolOrigin.Found), null, []);

        Assert.Equal("Karlach's portrait.png", new PoolPictureViewModel(picture, _ => { }).Caption);
    }

    [Fact]
    public async Task Nothing_is_proposed_before_the_storyboard_is_approved()
    {
        var (_, screen, _) = await OpenAsync(id => new ReferencesView(id, StageState.NotStarted, null, [], ReferencePhase.List, [], [], [], false, Labels));

        Assert.False(screen.CanPropose);
        Assert.False(screen.ProposeCommand.CanExecute(null));
    }

    [Fact]
    public async Task While_one_action_runs_the_others_wait()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Places[0].SelectCommand.Execute(null);
        var gate = new TaskCompletionSource();
        _client.ReferenceActionGate = gate.Task;

        var searching = screen.SearchAgainCommand.ExecuteAsync(null);

        Assert.False(screen.RemoveCommand.CanExecute(null));
        Assert.False(screen.StartEditLookCommand.CanExecute(null));
        Assert.False(screen.AddReferenceCommand.CanExecute(null));
        gate.SetResult();
        await searching;
        Assert.True(screen.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task References_without_an_image_can_have_their_images_made()
    {
        // The run was cancelled before the soul coin's image: the list is past checking, but one has nothing yet.
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R02" ? r with { Images = [] } : r)],
        });

        Assert.True(screen.MakeImagesCommand.CanExecute(null));
        Assert.True(screen.ShowsListBar);
    }

    [Fact]
    public async Task Your_own_picture_can_be_added_to_any_reference()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Characters[0].SelectCommand.Execute(null);
        _picked = @"C:rt\karlach.png";

        await screen.AddOwnPictureCommand.ExecuteAsync(null);

        Assert.Equal(("own", "R01"), _client.ReferenceActions.Single());
    }

    [Fact]
    public async Task A_failed_run_past_the_list_is_retried_by_making_the_missing_images()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id, StageState.Failed));

        await screen.RetryCommand.ExecuteAsync(null);

        Assert.Equal(("make images", ""), _client.ReferenceActions.Single());
        Assert.Empty(_client.Regenerated);   // a whole run would make the approved images again
    }

    [Fact]
    public async Task With_nothing_on_the_list_the_stage_is_approved_as_it_is()
    {
        var (_, screen, project) = await OpenAsync(id => Images(id) with { References = [] });

        Assert.True(screen.ShowsTextOnlyBar);
        Assert.False(screen.ShowsGateBar);
        await screen.ApproveTextOnlyCommand.ExecuteAsync(null);
        Assert.Equal((project.Id, PipelineStage.References, 3), _client.Approved.Single());
    }

    [Fact]
    public async Task An_empty_list_can_still_be_confirmed()
    {
        var (_, screen, _) = await OpenAsync(id => Listed(id) with { References = [] });

        Assert.True(screen.MakeImagesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_look_being_edited_is_put_away_when_a_pool_picture_is_picked()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Characters[0].SelectCommand.Execute(null);
        screen.StartEditLookCommand.Execute(null);

        screen.Pool[0].SelectCommand.Execute(null);

        Assert.False(screen.IsEditingLook);
        Assert.False(screen.SaveLookCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_image_whose_first_make_failed_can_be_made_from_the_bar()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R02" ? r with { Images = [Image("R02", ImageMaking.FromPicture, StageState.Failed, version: 0)] } : r)],
        });

        Assert.True(screen.ShowsListBar);
        Assert.True(screen.MakeImagesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_card_shows_its_approved_image_when_the_first_has_none()
    {
        var approved = Image("R03", ImageMaking.FromPicture, StageState.Approved) with { Key = "R03/2", Number = 2 };
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R03" ? r with { Images = [.. r.Images, approved] } : r)],
        });

        var card = screen.Places.Single();
        Assert.Equal("approved", card.StateText);
        Assert.False(card.IsMissing);
    }

    [Fact]
    public async Task A_card_shows_the_picture_of_the_image_it_stands_for()
    {
        var approved = Image("R03", ImageMaking.FromPicture, StageState.Approved) with { Key = "R03/2", Number = 2, Path = @"C:\p\R03-2.png" };
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R03" ? r with { Images = [.. r.Images, approved] } : r)],
        });

        Assert.Equal(@"C:\p\R03-2.png", screen.Places.Single().Thumbnail);
    }

    [Fact]
    public async Task While_an_image_is_made_the_list_and_the_pool_wait()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R02" ? r with { Images = [Image("R02", ImageMaking.FromPicture, StageState.Running)] } : r)],
        });
        screen.NewName = "Raphael";
        screen.Pool[0].SelectCommand.Execute(null);

        Assert.False(screen.AddReferenceCommand.CanExecute(null));
        Assert.False(screen.MakeReferenceCommand.CanExecute("A tavern mug"));
        Assert.False(screen.DiscardPictureCommand.CanExecute(null));
    }

    [Fact]
    public async Task One_of_several_images_can_be_removed()
    {
        var second = Image("R01", ImageMaking.NoPicture, missing: "None of the pictures found shows Karlach.") with { Key = "R01/2", Number = 2 };
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R01" ? r with { Images = [.. r.Images, second] } : r)],
        });
        screen.Characters[0].SelectCommand.Execute(null);
        screen.SelectImageCommand.Execute(2);

        await screen.RemoveImageCommand.ExecuteAsync(null);

        Assert.Equal(new ReferenceChange(ReferenceChangeKind.RemoveImage, ImageKey: "R01/2"), _client.ReferenceChanges.Single());
    }

    [Fact]
    public async Task The_only_image_cannot_be_removed()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        screen.Places[0].SelectCommand.Execute(null);

        Assert.False(screen.RemoveImageCommand.CanExecute(null));
    }

    [Fact]
    public void A_picture_unchanged_on_disk_is_decoded_once()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"storyforge-thumb-{Guid.NewGuid():N}.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
            System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, new byte[4], 2)));
        using (var file = System.IO.File.Create(path))
        {
            encoder.Save(file);
        }
        var converter = new StoryForge.App.Views.ThumbnailConverter();

        try
        {
            var first = converter.Convert(path, typeof(object), "400", System.Globalization.CultureInfo.InvariantCulture);
            var second = converter.Convert(path, typeof(object), "400", System.Globalization.CultureInfo.InvariantCulture);

            Assert.NotNull(first);
            Assert.Same(first, second);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task The_image_a_card_stands_for_is_the_one_marked_on_it()
    {
        var approved = Image("R03", ImageMaking.FromPicture, StageState.Approved) with { Key = "R03/2", Number = 2 };
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R03" ? r with { Images = [.. r.Images, approved] } : r)],
        });

        Assert.Equal([false, true], screen.Places.Single().Images.Select(i => i.IsShown));
    }

    [Fact]
    public void Decoded_pictures_kept_for_the_screen_stay_within_a_memory_budget()
    {
        Assert.True(StoryForge.App.Views.ThumbnailConverter.MaxCachedBytes <= 128L * 1024 * 1024);
        var kept = StoryForge.App.Views.ThumbnailConverter.Keep(
            new Dictionary<string, long> { ["a"] = 60, ["b"] = 30, ["c"] = 30 }, ["a", "b", "c"], budget: 100);

        Assert.Equal(["b", "c"], kept);   // the oldest goes first
    }

    [Fact]
    public void A_Fandom_picture_without_a_caption_shows_its_file_name_not_its_revision()
    {
        var picture = new PoolImageView(new PoolImage("P01", "https://static.wikia.nocookie.net/forgottenrealms/images/5/5e/Karlach_portrait.png/revision/latest?cb=20230801", "https://forgottenrealms.fandom.com/wiki/Karlach", "", 997, 1148, "pool/P01.png", PoolOrigin.Found), null, []);

        Assert.Equal("Karlach portrait.png", new PoolPictureViewModel(picture, _ => { }).Caption);
    }

    [Fact]
    public async Task One_image_being_made_shows_in_the_bar_and_can_be_cancelled()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R02" ? r with { Images = [Image("R02", ImageMaking.PartlyMadeUp, StageState.Running)] } : r)],
        });

        Assert.True(screen.IsMaking);
        await screen.CancelCommand.ExecuteAsync(null);

        Assert.Equal([("cancel", "R02/1")], _client.ShotActions);
    }

    [Fact]
    public async Task Nothing_is_added_to_a_list_that_was_not_proposed_yet()
    {
        var (_, screen, _) = await OpenAsync(id => new ReferencesView(id, StageState.NotStarted, null, [], ReferencePhase.List, [], [], [], false, Labels, StoryboardApproved: true));

        screen.NewName = "Karlach";

        Assert.False(screen.AddReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_name_that_was_added_is_cleared_even_when_the_screen_could_not_reload()
    {
        var (_, screen, _) = await OpenAsync(id => Images(id));
        _client.ReferencesLoadFailure = "The database is busy.";
        screen.NewName = "Gale";

        await screen.AddReferenceCommand.ExecuteAsync(null);

        Assert.Equal("Gale", _client.ReferenceChanges.Single().Name);
        Assert.Equal("", screen.NewName);
    }

    [Fact]
    public async Task A_card_stands_for_an_image_that_was_made_rather_than_one_that_shows_nothing()
    {
        var made = Image("R03", ImageMaking.FromPicture) with { Key = "R03/2", Number = 2 };
        var (_, screen, _) = await OpenAsync(id => Images(id) with
        {
            References = [.. Images(id).References.Select(r => r.Reference.Id == "R03" ? r with { Images = [.. r.Images, made] } : r)],
        });

        Assert.Equal([false, true], screen.Places.Single().Images.Select(i => i.IsShown));
    }
}
