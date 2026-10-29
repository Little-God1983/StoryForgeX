using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.NewProject;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class NewProjectPageTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static ProfileContent Empty => ProfileContent.Empty;

    /// <summary>One profile of every kind, so the screen can start a project.</summary>
    private void AddOneOfEach()
    {
        _client.AddProfile(ProfileKind.Research, "Lore", Empty with { ResearchSources = ["bg3.wiki", "forgottenrealms.fandom.com"] });
        _client.AddProfile(ProfileKind.Script, "Lore explainer", Empty, Empty);
        _client.AddProfile(ProfileKind.Storyboard, "Shot planner", Empty);
        _client.AddProfile(ProfileKind.Voice, "Calm narrator", Empty);
        _client.AddProfile(ProfileKind.Image, "Painted dark fantasy", Empty, Empty,
            Empty with { Sizes = [new("16:9", 1344, 768), new("9:16", 768, 1344)] });
        _client.AddProfile(ProfileKind.Video, "Slow cinematic push", Empty with { Sizes = [new("16:9", 1280, 720)], MaxClipSeconds = 30 });
    }

    private async Task<NewProjectPageViewModel> ShownPage()
    {
        var page = new NewProjectPageViewModel(_client);
        await page.ShowAsync();
        return page;
    }

    private static void FillBrief(NewProjectPageViewModel page)
    {
        page.Name = "Soul Coins – BG3 lore";
        page.Brief = "A lore video about the soul coins from Baldur's Gate 3.";
    }

    [Fact]
    public async Task Each_picker_offers_the_profiles_of_its_kind_and_starts_on_the_first()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Script, "Story narration", Empty);

        var page = await ShownPage();

        Assert.Equal(["Lore explainer", "Story narration"], page.Script.Slots[0].Choices.Select(p => p.Name));
        Assert.Equal("Lore explainer", page.Script.Slots[0].Selected!.Name);
        Assert.Equal("Calm narrator", page.Voice.Slots[0].Selected!.Name);
    }

    [Fact]
    public async Task The_research_sources_start_as_the_research_profiles()
    {
        AddOneOfEach();

        var page = await ShownPage();

        Assert.Equal(["bg3.wiki", "forgottenrealms.fandom.com"], page.ResearchSources);
    }

    [Fact]
    public async Task Sources_can_be_added_once_and_removed()
    {
        AddOneOfEach();
        var page = await ShownPage();

        page.NewSource = "  wikipedia.org ";
        page.AddSourceCommand.Execute(null);
        page.NewSource = "BG3.wiki";
        page.AddSourceCommand.Execute(null);
        page.RemoveSourceCommand.Execute("forgottenrealms.fandom.com");

        Assert.Equal(["bg3.wiki", "wikipedia.org"], page.ResearchSources);
        Assert.Equal("", page.NewSource);
    }

    [Fact]
    public async Task Stills_and_clips_start_at_the_delivery_size_rounded_up_for_the_models()
    {
        // Made at the delivery size, not upscaled later; models take multiples of 32, so 1080 becomes 1088.
        AddOneOfEach();

        var page = await ShownPage();

        Assert.Equal(new GenerationSize("16:9", 1920, 1080), page.SelectedDelivery.Value);
        Assert.Equal(new GenerationSize("16:9", 1920, 1088), page.StillSize.Value);
        Assert.Equal(new GenerationSize("16:9", 1920, 1088), page.ClipSize.Value);
        Assert.Equal(30, page.MaxClip.Value);
        Assert.Equal("1920 × 1088 · delivery size", page.StillSize.Options[0].Label);
    }

    [Theory]
    [InlineData("16:9", "1366 × 768", "1920 × 1080", "2560 × 1440")]
    [InlineData("9:16", "768 × 1366", "1080 × 1920", "1440 × 2560")]
    [InlineData("1:1", "768 × 768", "1080 × 1080", "1440 × 1440")]
    public async Task The_deliveries_are_768p_1080p_and_1440p(string aspect, string small, string full, string large)
    {
        var page = await ShownPage();

        page.Aspect = aspect;

        Assert.Equal([small, full, large], page.DeliveryChoices.Select(c => c.Label));
    }

    [Fact]
    public async Task Another_delivery_takes_the_sizes_along_and_keeps_a_custom_one()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.ClipSize.CustomWidth = "1600";
        page.ClipSize.CustomHeight = "896";

        page.SelectedDelivery = page.DeliveryChoices[0];

        Assert.Equal(new GenerationSize("16:9", 1376, 768), page.StillSize.Value);   // 1366 rounded up to 32
        Assert.Equal("1376 × 768 · delivery size", page.StillSize.Options[0].Label);
        Assert.Equal(new GenerationSize("16:9", 1600, 896), page.ClipSize.Value);
    }

    [Fact]
    public async Task Custom_left_as_it_was_filled_in_follows_another_delivery()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;   // 1920 × 1088, not touched

        page.SelectedDelivery = page.DeliveryChoices[2];   // 2560 × 1440

        Assert.Equal(new GenerationSize("16:9", 2560, 1440), page.ClipSize.Value);
        Assert.Equal("2560", page.ClipSize.CustomWidth);
    }

    [Fact]
    public async Task A_custom_size_off_the_32_pixel_grid_keeps_start_off()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.ClipSize.CustomWidth = "1366";
        page.ClipSize.CustomHeight = "768";

        Assert.Null(page.ClipSize.Value);
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("multiples of 32", page.MissingText);
    }

    [Fact]
    public async Task Custom_picked_again_after_another_delivery_starts_from_the_new_size()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;   // fills in 1920 × 1088
        page.ClipSize.SelectedIndex = GenerationSizeViewModel.DefaultIndex;

        page.SelectedDelivery = page.DeliveryChoices[2];   // 2560 × 1440
        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;

        Assert.Equal(new GenerationSize("16:9", 2560, 1440), page.ClipSize.Value);
        Assert.Null(page.ClipSize.Warning);
    }

    [Fact]
    public async Task A_custom_size_smaller_than_the_delivery_says_it_will_be_scaled_up()
    {
        AddOneOfEach();
        var page = await ShownPage();
        Assert.Null(page.ClipSize.Warning);

        page.ClipSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.ClipSize.CustomWidth = "1280";
        page.ClipSize.CustomHeight = "704";

        Assert.Equal("Smaller than the delivery: Assembly scales the clips up.", page.ClipSize.Warning);
        page.SelectedDelivery = page.DeliveryChoices[0];   // 1366 × 768: still smaller
        Assert.NotNull(page.ClipSize.Warning);
        page.ClipSize.CustomWidth = "1920";
        page.ClipSize.CustomHeight = "1088";
        Assert.NotNull(page.ClipSize.Value);
        Assert.Null(page.ClipSize.Warning);
        Assert.True(page.StillSize.Warning is null);
    }

    [Fact]
    public async Task Choosing_9_16_switches_to_a_portrait_delivery_and_its_sizes()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.StillSize.CustomWidth = "1500";

        page.Aspect = "9:16";
        await page.WhenSettled();

        Assert.Equal(new GenerationSize("9:16", 1088, 1920), page.StillSize.Value);
        Assert.False(page.StillSize.IsCustom);   // a custom size typed for 16:9 is dropped
        Assert.Equal(new GenerationSize("9:16", 1088, 1920), page.ClipSize.Value);
        Assert.Equal(new GenerationSize("9:16", 1080, 1920), page.SelectedDelivery.Value);
    }

    [Fact]
    public async Task The_size_and_clip_length_dropdowns_keep_their_entries_and_relabel_them()
    {
        // Seen on screen: rebuilding the options when the profile loaded made the dropdown drop
        // its selection, and the field showed blank. The entries stay; only the text changes.
        AddOneOfEach();
        var page = await ShownPage();
        var entries = page.StillSize.Options.ToList();
        var clipEntries = page.MaxClip.Options.ToList();

        page.StillSize.SelectedIndex = -1;   // what WPF writes when it loses the selection
        page.Aspect = "9:16";
        await page.WhenSettled();
        page.MaxClip.UseDefault(null);

        Assert.Equal(entries, page.StillSize.Options);
        Assert.Equal(clipEntries, page.MaxClip.Options);
        Assert.Equal("1088 × 1920 · delivery size", page.StillSize.Options[0].Label);
        Assert.Equal("20 s · built-in default", page.MaxClip.Options[0].Label);
        Assert.Equal(GenerationSizeViewModel.DefaultIndex, page.StillSize.SelectedIndex);
    }

    [Theory]
    [InlineData("16:9", 2560, 1440, "1:1", 1440, 1440)]
    [InlineData("1:1", 768, 768, "16:9", 1366, 768)]
    [InlineData("16:9", 2560, 1440, "9:16", 1440, 2560)]
    public async Task Another_aspect_keeps_the_delivery_tier(string from, int w, int h, string to, int expectedW, int expectedH)
    {
        var page = await ShownPage();
        page.Aspect = from;
        page.SelectedDelivery = page.DeliveryChoices.Single(c => c.Value.Width == w && c.Value.Height == h);

        page.Aspect = to;

        Assert.Equal(new GenerationSize(to, expectedW, expectedH), page.SelectedDelivery.Value);
    }

    [Fact]
    public async Task Sizes_loading_for_an_earlier_aspect_do_not_land_after_a_later_one()
    {
        AddOneOfEach();
        var page = await ShownPage();
        var slow = new TaskCompletionSource();
        _client.ProfileLoadGate = slow.Task;

        page.Aspect = "9:16";        // its profile reads wait
        _client.ProfileLoadGate = null;
        page.Aspect = "1:1";         // its reads finish at once
        slow.SetResult();
        await page.WhenSettled();

        Assert.Equal("1:1", page.StillSize.Value!.Aspect);
        Assert.Equal("1:1", page.ClipSize.Value!.Aspect);
    }

    [Fact]
    public async Task Coming_back_to_the_screen_keeps_typed_sources_and_custom_sizes()
    {
        AddOneOfEach();
        var page = await ShownPage();
        // What WPF does: a dropdown whose list is cleared writes null into its SelectedItem.
        foreach (var slot in page.Cards.SelectMany(c => c.Slots))
        {
            slot.Choices.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                {
                    slot.Selected = null;
                }
            };
        }
        page.NewSource = "wikipedia.org";
        page.AddSourceCommand.Execute(null);
        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.StillSize.CustomWidth = "1600";

        await page.ShowAsync();

        Assert.Contains("wikipedia.org", page.ResearchSources);
        Assert.Equal(1600, page.StillSize.Value!.Width);
        Assert.Equal("Lore", page.ResearchSlot.Selected!.Name);
    }

    [Theory]
    [InlineData("0.005")]
    [InlineData("0")]
    [InlineData("abc")]
    public async Task A_custom_target_length_under_a_second_keeps_start_off(string minutes)
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.TargetSeconds = NewProjectPageViewModel.CustomTarget;
        page.CustomTargetMinutes = minutes;

        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("target length", page.MissingText);
    }

    [Fact]
    public async Task Another_aspect_switches_the_sizes_at_once_without_loading_anything()
    {
        // Nothing to wait for, so Start can never send sizes for the old aspect.
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);
        _client.ProfileLoadGate = new TaskCompletionSource().Task;   // any load would hang

        page.Aspect = "9:16";

        Assert.Equal(new GenerationSize("9:16", 1088, 1920), page.StillSize.Value);
        Assert.Equal("9:16", page.ClipSize.Value!.Aspect);
        Assert.True(page.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_profile_whose_content_failed_to_load_is_tried_again_when_the_screen_opens()
    {
        AddOneOfEach();
        _client.ProfileLoadFailure = new InvalidOperationException("database is locked");
        var page = await ShownPage();
        Assert.Contains("database is locked", page.Error);

        _client.ProfileLoadFailure = null;
        await page.ShowAsync();

        Assert.Equal(["bg3.wiki", "forgottenrealms.fandom.com"], page.ResearchSources);
        Assert.Equal(30, page.MaxClip.Value);
    }

    [Fact]
    public async Task Start_waits_for_a_newly_picked_profiles_defaults()
    {
        // Otherwise the new profile would be saved with the old profile's clip length.
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Video, "Wan 2.2", Empty with { MaxClipSeconds = 6 });
        var page = await ShownPage();
        FillBrief(page);
        var loading = new TaskCompletionSource();
        _client.ProfileLoadGate = loading.Task;

        page.VideoSlot.Selected = page.VideoSlot.Choices.Single(p => p.Name == "Wan 2.2");

        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("Wan 2.2", page.MissingText);
        _client.ProfileLoadGate = null;
        loading.SetResult();
        await page.WhenSettled();
        Assert.True(page.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_stays_off_while_a_picked_profiles_defaults_failed_to_load()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Video, "Wan 2.2", Empty with { MaxClipSeconds = 6 });
        var page = await ShownPage();
        FillBrief(page);
        _client.ProfileLoadFailure = new InvalidOperationException("database is locked");

        page.VideoSlot.Selected = page.VideoSlot.Choices.Single(p => p.Name == "Wan 2.2");
        await page.WhenSettled();

        Assert.False(page.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_image_profile_that_cannot_be_read_does_not_hold_up_start()
    {
        // Nothing from its content goes into the setup: the sizes follow the delivery.
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Image, "Photoreal", Empty);
        var page = await ShownPage();
        FillBrief(page);
        _client.ProfileLoadFailure = new InvalidOperationException("database is locked");

        page.ImageSlot.Selected = page.ImageSlot.Choices.Single(p => p.Name == "Photoreal");
        await page.WhenSettled();

        Assert.True(page.StartCommand.CanExecute(null));
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task A_custom_size_must_have_the_videos_shape()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);
        page.Aspect = "9:16";

        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.StillSize.CustomWidth = "1344";
        page.StillSize.CustomHeight = "768";

        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("9:16", page.MissingText);
    }

    [Fact]
    public async Task A_new_version_of_the_same_profile_keeps_typed_sources_and_custom_values()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.NewSource = "wikipedia.org";
        page.AddSourceCommand.Execute(null);
        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.StillSize.CustomWidth = "1600";
        page.MaxClip.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.MaxClip.CustomSeconds = "12";
        var research = page.ResearchSlot.Selected!;
        var image = page.ImageSlot.Selected!;
        var video = page.VideoSlot.Selected!;
        await _client.SaveProfileVersionAsync(research.Id, Empty with { ResearchSources = ["bg3.wiki", "eldenring.wiki"] });
        await _client.SaveProfileVersionAsync(image.Id, Empty with { Sizes = [new("16:9", 1920, 1080)] });
        await _client.SaveProfileVersionAsync(video.Id, Empty with { MaxClipSeconds = 10 });

        await page.ShowAsync();

        Assert.Equal(["bg3.wiki", "eldenring.wiki", "wikipedia.org"], page.ResearchSources);
        Assert.Equal(1600, page.StillSize.Value!.Width);
        Assert.Equal(12, page.MaxClip.Value);
        Assert.Equal(4, page.ImageSlot.Selected!.LatestVersion);   // it was at v3
    }

    [Fact]
    public async Task Another_video_profile_drops_a_custom_clip_length_like_the_size()
    {
        // A length typed for one model may be too long for the next one.
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Video, "Wan 2.2", Empty with { MaxClipSeconds = 6 });
        var page = await ShownPage();
        page.MaxClip.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.MaxClip.CustomSeconds = "25";

        page.VideoSlot.Selected = page.VideoSlot.Choices.Single(p => p.Name == "Wan 2.2");
        await page.WhenSettled();

        Assert.False(page.MaxClip.IsCustom);
        Assert.Equal(6, page.MaxClip.Value);
    }

    [Fact]
    public async Task A_custom_size_is_possible_and_must_be_a_number()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        Assert.Equal("1920", page.StillSize.CustomWidth);   // starts from the default
        page.StillSize.CustomWidth = "13x4";
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("still images", page.MissingText);
        page.StillSize.CustomWidth = "1536";

        Assert.Equal(new GenerationSize("16:9", 1536, 1088), page.StillSize.Value);
        Assert.True(page.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_stays_off_until_name_brief_and_every_profile_are_there_and_says_what_is_missing()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Empty);
        var page = await ShownPage();

        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Equal(
            "Needs a name, a brief, a research profile, a script profile, a storyboard profile, a voice profile and a video profile.",
            page.MissingText);
    }

    [Fact]
    public async Task Later_stages_takes_over_provider_model_and_same_named_profiles_where_they_exist()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Image, "Noir", Empty);
        _client.AddProfile(ProfileKind.Video, "Noir", Empty);
        var page = await ShownPage();
        var stillsBefore = page.Stills.Model;

        page.ImageSlot.Selected = page.ImageSlot.Choices.Single(p => p.Name == "Noir");
        page.Stills.Model = "Krea 2 Turbo";
        page.ApplyToLaterStagesCommand.Execute(page.Stills);

        Assert.Equal("Noir", page.VideoSlot.Selected!.Name);
        Assert.Equal("Minimax H3", page.Clips.Model);           // Krea is not a video model: kept
        Assert.Equal("Calm narrator", page.Voice.Slots[0].Selected!.Name);   // an earlier stage: untouched
        Assert.NotEqual(stillsBefore, page.Stills.Model);
    }

    [Fact]
    public async Task All_also_reaches_earlier_stages_but_keeps_choices_without_a_match()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Script, "Noir", Empty);
        _client.AddProfile(ProfileKind.Video, "Noir", Empty);
        var page = await ShownPage();
        page.VideoSlot.Selected = page.VideoSlot.Choices.Single(p => p.Name == "Noir");

        page.ApplyToAllCommand.Execute(page.Clips);

        Assert.Equal("Noir", page.Script.Slots[0].Selected!.Name);
        Assert.Equal("Shot planner", page.StoryboardSlot.Selected!.Name);   // no storyboard "Noir"
        Assert.Equal("Claude CLI", page.Script.Provider);                   // ComfyUI is no text provider
        Assert.Equal("Claude CLI", page.Research.Provider);
    }

    [Fact]
    public async Task Research_and_script_each_have_their_own_provider_and_model()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.Script.Provider = "LM Studio";
        await page.StartCommand.ExecuteAsync(null);

        var setup = Assert.Single(_client.Projects).Setup;
        Assert.Equal("Claude CLI", setup.Research.Provider);
        Assert.Equal("LM Studio", setup.Script.Provider);
    }

    [Fact]
    public async Task Later_stages_from_research_reaches_the_script_card()
    {
        AddOneOfEach();
        var page = await ShownPage();

        page.Research.Provider = "LM Studio";
        page.ApplyToLaterStagesCommand.Execute(page.Research);

        Assert.Equal("LM Studio", page.Script.Provider);
        Assert.Equal("ComfyUI", page.Voice.Provider);   // no text provider: kept
    }

    [Fact]
    public async Task The_image_references_card_picks_its_own_image_profile_and_later_stages_carries_it_to_the_stills()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Image, "Qwen edit", Empty);
        var page = await ShownPage();
        var references = page.References.Slots[0];
        Assert.Equal(["Painted dark fantasy", "Qwen edit"], references.Choices.Select(p => p.Name));

        references.Selected = references.Choices.Single(p => p.Name == "Qwen edit");
        Assert.Equal("Painted dark fantasy", page.ImageSlot.Selected!.Name);   // the stills keep theirs

        page.ApplyToLaterStagesCommand.Execute(page.References);

        Assert.Equal("Qwen edit", page.ImageSlot.Selected!.Name);
        Assert.Equal("Qwen Image 2.1", page.Stills.Model);   // an edit model is no stills model: kept
    }

    [Fact]
    public async Task The_run_plan_names_the_reference_model()
    {
        var page = await ShownPage();
        var references = page.RunPlan.Single(s => s.Stage == PipelineStage.References);
        Assert.StartsWith("Qwen Image 2.1 edit", references.Via);

        page.References.Model = "Krea 2 edit";
        Assert.StartsWith("Krea 2 edit", references.Via);

        page.Consistency = Consistency.TextToImageOnly;
        Assert.DoesNotContain("Krea", references.Via);
    }

    [Fact]
    public async Task The_storyboard_and_references_gates_cannot_be_switched_off()
    {
        var page = await ShownPage();
        var storyboard = page.RunPlan.Single(s => s.Stage == PipelineStage.Storyboard);
        var voice = page.RunPlan.Single(s => s.Stage == PipelineStage.Voice);

        storyboard.IsGated = false;
        voice.IsGated = true;

        Assert.True(storyboard.IsGated);
        Assert.False(storyboard.CanChangeGate);
        Assert.True(page.RunPlan.Single(s => s.Stage == PipelineStage.References).IsGated);
        Assert.True(voice.IsGated);
        Assert.Equal("References", page.RunPlan.Single(s => s.Stage == PipelineStage.References).Name);
    }

    [Fact]
    public async Task Start_saves_every_choice_with_the_exact_profile_versions()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);
        page.Aspect = "9:16";
        await page.WhenSettled();
        page.TargetSeconds = NewProjectPageViewModel.CustomTarget;
        page.CustomTargetMinutes = "2.5";
        page.Language = "Deutsch";
        page.Assembly = AssemblyTarget.DavinciResolve;
        page.Consistency = Consistency.TextToImageOnly;
        page.References.Model = "Krea 2 edit";
        page.RunThrough = true;
        page.RunPlan.Single(s => s.Stage == PipelineStage.Clips).IsGated = true;
        Project? started = null;
        page.ProjectStarted += (_, p) => started = p;

        await page.StartCommand.ExecuteAsync(null);

        var setup = Assert.Single(_client.Projects).Setup;
        Assert.Same(_client.Projects[0], started);
        Assert.Equal("Soul Coins – BG3 lore", setup.Name);
        Assert.Equal(["bg3.wiki", "forgottenrealms.fandom.com"], setup.ResearchSources);
        Assert.Equal(2, setup.Script.Script.Version);              // the script profile is at v2
        Assert.Equal(3, setup.Stills.Profile.Version);
        Assert.Equal(new ReferencesSetup("ComfyUI", "Krea 2 edit", setup.Stills.Profile, Consistency.TextToImageOnly), setup.References);
        Assert.Equal((new GenerationSize("9:16", 1088, 1920), new GenerationSize("9:16", 1088, 1920)), (setup.Stills.Size, setup.Clips.Size));   // the delivery, rounded up to 32
        Assert.Equal(new OutputSetup("9:16", 1080, 1920, 150, "Deutsch", AssemblyTarget.DavinciResolve), setup.Output);
        Assert.Equal(30, setup.Clips.MaxClipSeconds);
        Assert.Equal(RunMode.RunThrough, setup.Mode);
        Assert.Contains(PipelineStage.Clips, setup.Gates);
        Assert.DoesNotContain(PipelineStage.Voice, setup.Gates);
        Assert.Equal("", page.Name);   // ready for the next project
    }

    [Fact]
    public async Task A_refused_start_says_why_and_keeps_everything()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);
        _client.ProjectCreateFailure = new ArgumentException("\"Calm narrator\" has no version 1.");

        await page.StartCommand.ExecuteAsync(null);

        Assert.Equal("\"Calm narrator\" has no version 1.", page.Error);
        Assert.Equal("Soul Coins – BG3 lore", page.Name);
    }

    [Fact]
    public async Task Starting_opens_the_project_on_its_fact_sheet_starts_the_run_and_lists_it()
    {
        AddOneOfEach();
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        var page = (NewProjectPageViewModel)main.CurrentPage;
        FillBrief(page);

        await page.StartCommand.ExecuteAsync(null);
        await main.PageShowing;

        var matrix = Assert.IsType<ResultMatrixPageViewModel>(main.CurrentPage);
        Assert.Equal("Result matrix", main.SelectedNavItem!.Title);
        Assert.Equal("Soul Coins – BG3 lore", matrix.Name);
        Assert.Equal("16:9 · 1920×1080 · target 4:00", matrix.Summary);
        Assert.Equal(9, matrix.Stages.Count);
        Assert.All(matrix.Stages, s => Assert.Equal("not started", s.StateText));
        Assert.True(matrix.ShowsFactSheet);
        Assert.Equal("Projects / Soul Coins – BG3 lore / Research", main.Breadcrumb);
        Assert.Equal([_client.Projects.Single().Id], _client.StartedRuns);
        Assert.Equal(["Soul Coins – BG3 lore"], main.RecentProjects.Select(p => p.Name));
    }

    [Fact]
    public async Task A_recent_project_opens_in_the_result_matrix()
    {
        AddOneOfEach();
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        var page = (NewProjectPageViewModel)main.CurrentPage;
        FillBrief(page);
        await page.StartCommand.ExecuteAsync(null);
        main.SelectedNavItem = main.NavItems[0];

        await main.OpenRecentProjectCommand.ExecuteAsync(main.RecentProjects[0]);

        Assert.IsType<ResultMatrixPageViewModel>(main.CurrentPage);
        Assert.Equal("Soul Coins – BG3 lore", ((ResultMatrixPageViewModel)main.CurrentPage).Name);
    }

    [Fact]
    public async Task A_recent_project_that_cannot_be_opened_leaves_the_open_one_on_screen()
    {
        AddOneOfEach();
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        var page = (NewProjectPageViewModel)main.CurrentPage;
        FillBrief(page);
        await page.StartCommand.ExecuteAsync(null);
        var matrix = (ResultMatrixPageViewModel)main.CurrentPage;

        await matrix.OpenAsync(Guid.NewGuid());   // a stale entry

        Assert.Equal("Soul Coins – BG3 lore", matrix.Name);
        Assert.Equal("Projects / Soul Coins – BG3 lore / Research", matrix.Breadcrumb);
        Assert.NotNull(matrix.LoadError);
    }

    [Fact]
    public async Task Profiles_made_since_show_up_when_the_screen_opens_again()
    {
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        _client.AddProfile(ProfileKind.Voice, "Calm narrator", Empty);

        main.SelectedNavItem = main.NavItems[2];
        main.SelectedNavItem = main.NavItems[0];
        await main.PageShowing;

        Assert.Equal("Calm narrator", ((NewProjectPageViewModel)main.CurrentPage).Voice.Slots[0].Selected!.Name);
    }

    [Fact]
    public async Task The_pacing_starts_as_the_storyboard_profiles_and_stays_out_of_the_setup()
    {
        AddOneOfEach();
        _client.AddProfile(ProfileKind.Storyboard, "Quick cuts", Empty with { Pacing = new Pacing(2, 3, 5) });
        var page = await ShownPage();
        page.StoryboardSlot.Selected = page.StoryboardSlot.Choices.Single(p => p.Name == "Quick cuts");
        await page.WhenSettled();
        FillBrief(page);

        Assert.Equal("2–5 s, about 3 s · profile default", page.Pacing.Options[0].Label);
        await page.StartCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(_client.Projects).Setup.Pacing);
    }

    [Fact]
    public async Task A_profile_without_a_pacing_uses_the_built_in_one()
    {
        AddOneOfEach();

        var page = await ShownPage();

        Assert.Equal("3–8 s, about 5 s · built-in default", page.Pacing.Options[0].Label);
    }

    [Fact]
    public async Task A_custom_pacing_goes_into_the_setup()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.Pacing.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        Assert.Equal(("3", "5", "8"), (page.Pacing.CustomMin, page.Pacing.CustomTypical, page.Pacing.CustomMax));
        page.Pacing.CustomMin = "2";
        page.Pacing.CustomTypical = "4";
        page.Pacing.CustomMax = "6";
        await page.StartCommand.ExecuteAsync(null);

        Assert.Equal(new Pacing(2, 4, 6), Assert.Single(_client.Projects).Setup.Pacing);
    }

    [Theory]
    [InlineData("1", "4", "6")]
    [InlineData("5", "4", "6")]
    [InlineData("3", "5", "40")]
    [InlineData("3", "five", "8")]
    public async Task An_impossible_custom_pacing_keeps_start_off(string min, string typical, string max)
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.Pacing.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        (page.Pacing.CustomMin, page.Pacing.CustomTypical, page.Pacing.CustomMax) = (min, typical, max);

        Assert.False(page.CanStart);
        Assert.Contains("a valid pacing", page.MissingText);
    }

    [Fact]
    public async Task The_shot_estimate_follows_the_target_length_and_the_pacing()
    {
        AddOneOfEach();
        var page = await ShownPage();

        Assert.Equal("about 48 shots → 48 stills, 48 clips", page.ShotEstimate);   // 4:00 at about 5 s
        page.TargetSeconds = 60;
        Assert.Equal("about 12 shots → 12 stills, 12 clips", page.ShotEstimate);
        page.Pacing.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.Pacing.CustomTypical = "4";
        Assert.Equal("about 15 shots → 15 stills, 15 clips", page.ShotEstimate);
    }

    [Fact]
    public async Task A_profile_pacing_longer_than_the_max_clip_is_held_to_it_and_start_stays_on()
    {
        // The storyboard holds the profile's 3–8 s to clips of 6 s: nothing for you to fix.
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.MaxClip.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.MaxClip.CustomSeconds = "6";

        Assert.Null(page.PacingProblem);
        Assert.True(page.CanStart);
    }

    [Fact]
    public async Task A_custom_pacing_longer_than_the_max_clip_keeps_start_off()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.Pacing.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.Pacing.CustomMax = "40";   // the video profile makes 30 s

        Assert.Equal("Shots of up to 40 s are longer than the video model makes (30 s).", page.PacingProblem);
        Assert.False(page.CanStart);
    }

    [Fact]
    public async Task The_shot_estimate_counts_with_the_pacing_held_to_the_clips()
    {
        AddOneOfEach();
        var page = await ShownPage();

        page.MaxClip.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.MaxClip.CustomSeconds = "4";   // the profile's about 5 s becomes about 4 s

        Assert.Equal("about 60 shots → 60 stills, 60 clips", page.ShotEstimate);
    }

    [Fact]
    public async Task The_reference_list_is_reviewed_first_unless_you_untick_it()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);
        Assert.True(page.ReviewReferenceList);

        page.ReviewReferenceList = false;
        await page.StartCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(_client.Projects).Setup.References.ReviewList);
    }
}
