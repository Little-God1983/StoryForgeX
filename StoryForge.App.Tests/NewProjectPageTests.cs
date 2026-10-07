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

        Assert.Equal(["Lore explainer", "Story narration"], page.Writing.Slots[1].Choices.Select(p => p.Name));
        Assert.Equal("Lore explainer", page.Writing.Slots[1].Selected!.Name);
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
    public async Task Generation_sizes_and_clip_length_start_as_the_profiles()
    {
        AddOneOfEach();

        var page = await ShownPage();

        Assert.Equal(new GenerationSize("16:9", 1344, 768), page.StillSize.Value);
        Assert.Equal(new GenerationSize("16:9", 1280, 720), page.ClipSize.Value);
        Assert.Equal(30, page.MaxClip.Value);
        Assert.Equal("1344 × 768 · profile default", page.StillSize.Options[0].Label);
    }

    [Fact]
    public async Task Choosing_9_16_switches_to_the_profiles_9_16_sizes_and_a_portrait_delivery()
    {
        AddOneOfEach();
        var page = await ShownPage();
        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        page.StillSize.CustomWidth = "1500";

        page.Aspect = "9:16";
        await page.WhenSettled();

        Assert.Equal(new GenerationSize("9:16", 768, 1344), page.StillSize.Value);
        Assert.False(page.StillSize.IsCustom);
        // The video profile has no 9:16 size: the built-in one stands in, and says so.
        Assert.Equal(new GenerationSize("9:16", 768, 1344), page.ClipSize.Value);
        Assert.Contains("built-in", page.ClipSize.Options[0].Label);
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
        Assert.Equal("768 × 1344 · profile default", page.StillSize.Options[0].Label);
        Assert.Equal("20 s · built-in default", page.MaxClip.Options[0].Label);
        Assert.Equal(GenerationSizeViewModel.ProfileDefaultIndex, page.StillSize.SelectedIndex);
    }

    [Theory]
    [InlineData("16:9", 3840, 2160, "1:1", 2160, 2160)]
    [InlineData("1:1", 1440, 1440, "16:9", 2560, 1440)]
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

        Assert.Equal(new GenerationSize("9:16", 768, 1344), page.StillSize.Value);
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
    public async Task A_custom_size_is_possible_and_must_be_a_number()
    {
        AddOneOfEach();
        var page = await ShownPage();
        FillBrief(page);

        page.StillSize.SelectedIndex = GenerationSizeViewModel.CustomIndex;
        Assert.Equal("1344", page.StillSize.CustomWidth);   // starts from the default
        page.StillSize.CustomWidth = "13x4";
        Assert.False(page.StartCommand.CanExecute(null));
        Assert.Contains("generation size", page.MissingText);
        page.StillSize.CustomWidth = "1536";

        Assert.Equal(new GenerationSize("16:9", 1536, 768), page.StillSize.Value);
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

        Assert.Equal("Noir", page.Writing.Slots[1].Selected!.Name);
        Assert.Equal("Shot planner", page.Writing.Slots[2].Selected!.Name);   // no storyboard "Noir"
        Assert.Equal("Claude CLI", page.Writing.Provider);                    // ComfyUI is no text provider
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
        page.RunThrough = true;
        page.RunPlan.Single(s => s.Stage == PipelineStage.Clips).IsGated = true;
        Project? started = null;
        page.ProjectStarted += (_, p) => started = p;

        await page.StartCommand.ExecuteAsync(null);

        var setup = Assert.Single(_client.Projects).Setup;
        Assert.Same(_client.Projects[0], started);
        Assert.Equal("Soul Coins – BG3 lore", setup.Name);
        Assert.Equal(["bg3.wiki", "forgottenrealms.fandom.com"], setup.ResearchSources);
        Assert.Equal(2, setup.Writing.Script.Version);              // the script profile is at v2
        Assert.Equal(3, setup.Stills.Profile.Version);
        Assert.Equal(new GenerationSize("9:16", 768, 1344), setup.Stills.Size);
        Assert.Equal(new OutputSetup("9:16", 1080, 1920, 150, "Deutsch", AssemblyTarget.DavinciResolve), setup.Output);
        Assert.Equal(Consistency.TextToImageOnly, setup.Stills.Consistency);
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
    public async Task Starting_opens_the_project_in_the_result_matrix_and_lists_it()
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
        Assert.Equal("Projects / Soul Coins – BG3 lore", main.Breadcrumb);
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
        Assert.Equal("Projects / Soul Coins – BG3 lore", matrix.Breadcrumb);
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
}
