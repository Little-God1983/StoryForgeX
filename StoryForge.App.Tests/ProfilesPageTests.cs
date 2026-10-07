using StoryForge.App.ViewModels;
using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;

namespace StoryForge.App.Tests;

public sealed class ProfilesPageTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static ProfileContent Image(string prompt = "{shot.visual}") =>
        FakeStoryForgeClient.EmptyContent with
        {
            Provider = "ComfyUI",
            PromptTemplate = prompt,
            Inputs = [new("prompt", "#6.text")],
            Sizes = [new("16:9", 1344, 768)],
        };

    private async Task<ProfilesPageViewModel> LoadedPage()
    {
        var page = new ProfilesPageViewModel(_client);
        await page.LoadAsync();
        return page;
    }

    private static async Task Open(ProfilesPageViewModel page, string name)
    {
        page.SelectedProfile = page.Profiles.Single(p => p.Name == name);
        await page.Loading;
    }

    [Fact]
    public async Task The_list_shows_every_profile_by_kind_then_name_with_its_version()
    {
        _client.AddProfile(ProfileKind.Voice, "Calm narrator", FakeStoryForgeClient.EmptyContent);
        _client.AddProfile(ProfileKind.Image, "Painted dark fantasy", Image(), Image(), Image("v3"));
        _client.AddProfile(ProfileKind.Script, "Lore explainer", FakeStoryForgeClient.EmptyContent);

        var page = await LoadedPage();

        Assert.Equal(["Lore explainer · v1", "Painted dark fantasy · v3", "Calm narrator · v1"], page.Profiles.Select(p => p.Label));
        Assert.Equal(["Script (LLM)", "Image", "Voice"], page.Profiles.Select(p => p.KindLabel));
    }

    [Fact]
    public async Task A_list_that_cannot_be_loaded_says_why()
    {
        _client.ProfileListFailure = new InvalidOperationException("database is locked");

        var page = await LoadedPage();

        Assert.Contains("database is locked", page.LoadError);
        Assert.False(page.HasProfiles);
    }

    [Fact]
    public async Task Selecting_a_profile_opens_its_latest_version()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image("old"), Image("new"));
        var page = await LoadedPage();

        await Open(page, "Painted");

        Assert.Equal("new", page.Editor!.Draft.PromptTemplate);
        Assert.Equal("v2 · saved", page.Editor.StateText);
        Assert.Equal("Save as v3", page.Editor.SaveText);
        Assert.Equal([2, 1], page.Editor.Versions);
        Assert.Equal("Settings / Profiles / Painted", page.Breadcrumb);
    }

    [Fact]
    public async Task Save_is_enabled_only_after_an_edit_and_undoing_the_edit_disables_it_again()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Draft.NegativePrompt = "watermark";
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Equal("v1 · unsaved changes", editor.StateText);
        editor.Draft.NegativePrompt = "";
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_stores_the_next_version_and_keeps_the_old_one_viewable()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        editor.Draft.PromptTemplate = "{shot.visual}, oil paint";
        editor.Draft.Sizes[0].Width = "1280";
        editor.Draft.Inputs[0].Node = "#9.text";
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = await _client.GetProfileVersionAsync(editor.Id);
        Assert.Equal(2, saved.Version);
        Assert.Equal("{shot.visual}, oil paint", saved.Content.PromptTemplate);
        Assert.Equal(1280, saved.Content.Sizes[0].Width);
        Assert.Equal("#9.text", saved.Content.Inputs[0].Node);
        Assert.Equal("v2 · saved", editor.StateText);
        Assert.Equal("Save as v3", editor.SaveText);
        Assert.Equal([2, 1], editor.Versions);
        Assert.Equal("Painted · v2", page.Profiles.Single().Label);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_older_version_is_shown_read_only_and_can_be_restored_as_the_newest()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image("first"), Image("second"));
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        editor.SelectedVersion = 1;
        await editor.Loading;

        Assert.True(editor.IsViewingOlderVersion);
        Assert.True(editor.Shown.IsReadOnly);
        Assert.Equal("first", editor.Shown.PromptTemplate);
        Assert.Equal("v1 · read only", editor.StateText);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.RestoreShownVersionCommand.Execute(null);
        Assert.False(editor.IsViewingOlderVersion);
        Assert.Equal("first", editor.Draft.PromptTemplate);
        Assert.Equal(2, editor.SelectedVersion);
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("first", (await _client.GetProfileVersionAsync(editor.Id, 3)).Content.PromptTemplate);
        Assert.Equal("second", (await _client.GetProfileVersionAsync(editor.Id, 2)).Content.PromptTemplate);
    }

    [Fact]
    public async Task Looking_at_an_older_version_keeps_the_unsaved_draft()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image("first"), Image("second"));
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;
        editor.Draft.PromptTemplate = "draft";

        editor.SelectedVersion = 1;
        await editor.Loading;
        editor.ShowLatestCommand.Execute(null);

        Assert.Same(editor.Draft, editor.Shown);
        Assert.Equal("draft", editor.Draft.PromptTemplate);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task Unsaved_edits_stay_with_their_profile_while_another_is_open()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        _client.AddProfile(ProfileKind.Image, "Photoreal", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        page.Editor!.Draft.NegativePrompt = "logo";

        await Open(page, "Photoreal");
        await Open(page, "Painted");

        Assert.Equal("logo", page.Editor!.Draft.NegativePrompt);
        Assert.True(page.Profiles.Single(p => p.Name == "Painted").HasUnsavedChanges);
        Assert.False(page.Profiles.Single(p => p.Name == "Photoreal").HasUnsavedChanges);
        Assert.True(page.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_profile_that_loads_after_another_was_picked_does_not_replace_it()
    {
        _client.AddProfile(ProfileKind.Image, "Slow", Image("slow"));
        _client.AddProfile(ProfileKind.Image, "Fast", Image("fast"));
        var page = await LoadedPage();
        var gate = new TaskCompletionSource();
        _client.ProfileLoadGate = gate.Task;

        page.SelectedProfile = page.Profiles.Single(p => p.Name == "Slow");
        var slow = page.Loading;
        _client.ProfileLoadGate = null;
        await Open(page, "Fast");
        gate.SetResult();
        await slow;

        Assert.Equal("fast", page.Editor!.Draft.PromptTemplate);
        Assert.Equal("Settings / Profiles / Fast", page.Breadcrumb);
    }

    [Fact]
    public async Task A_profile_that_cannot_be_loaded_says_why()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        _client.ProfileLoadFailure = new InvalidOperationException("disk gone");

        await Open(page, "Painted");

        Assert.Null(page.Editor);
        Assert.Contains("disk gone", page.LoadError);
    }

    [Fact]
    public async Task A_profile_that_cannot_be_loaded_does_not_leave_the_previous_name_in_the_breadcrumb()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        _client.AddProfile(ProfileKind.Image, "Noir", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        _client.ProfileLoadFailure = new InvalidOperationException("disk gone");

        await Open(page, "Noir");

        Assert.Equal("Settings / Profiles", page.Breadcrumb);
    }

    [Fact]
    public async Task A_save_that_finishes_while_an_older_version_is_shown_leaves_that_version_on_screen()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image("first"), Image("second"));
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;
        var gate = new TaskCompletionSource();
        _client.ProfileSaveGate = gate.Task;
        editor.Draft.PromptTemplate = "third";

        var saving = editor.SaveCommand.ExecuteAsync(null);
        editor.SelectedVersion = 1;
        await editor.Loading;
        gate.SetResult();
        await saving;

        Assert.Equal(3, editor.LatestVersion);
        Assert.Equal([3, 2, 1], editor.Versions);
        Assert.Equal(1, editor.SelectedVersion);
        Assert.Equal("first", editor.Shown.PromptTemplate);
        Assert.Equal("v1 · read only", editor.StateText);
    }

    [Fact]
    public async Task A_version_that_cannot_be_loaded_puts_the_picker_back_on_what_is_shown()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image("first"), Image("second"));
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;
        _client.ProfileLoadFailure = new InvalidOperationException("disk gone");

        editor.SelectedVersion = 1;
        await editor.Loading;

        Assert.Equal(2, editor.SelectedVersion);
        Assert.Same(editor.Draft, editor.Shown);
        Assert.Equal("v2 · saved", editor.StateText);
        Assert.Contains("disk gone", editor.Error);
    }

    [Fact]
    public async Task A_dropdown_writing_null_changes_nothing()
    {
        // WPF's ComboBox writes null into SelectedItem when its list changes under it.
        _client.AddProfile(ProfileKind.Image, "Painted", Image() with { WorkflowTemplate = "qwen-image.json" });
        var page = await LoadedPage();
        await Open(page, "Painted");
        var form = page.Editor!.Draft;

        form.WorkflowTemplate = null!;
        form.Provider = null!;
        form.SetTemplates(["krea-turbo.json"]);

        Assert.Equal("qwen-image.json", form.WorkflowTemplate);
        Assert.Equal("ComfyUI", form.Provider);
        Assert.False(page.Editor.IsDirty);
    }

    [Fact]
    public async Task Spaces_the_engine_trims_away_are_no_change()
    {
        _client.AddProfile(ProfileKind.Voice, "Narrator", FakeStoryForgeClient.EmptyContent with { Provider = "ComfyUI", Voice = "Anna" });
        var page = await LoadedPage();
        await Open(page, "Narrator");
        var editor = page.Editor!;

        editor.Draft.Voice = "Anna ";

        Assert.False(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_older_template_list_arriving_late_does_not_replace_a_newer_one()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        _client.HoldTemplateReads = true;

        var older = page.RefreshTemplatesAsync();
        var newer = page.RefreshTemplatesAsync();
        var olderRead = _client.PendingTemplateReads.Dequeue();
        var newerRead = _client.PendingTemplateReads.Dequeue();
        newerRead.SetResult(["new-folder.json"]);
        olderRead.SetResult(["old-folder.json"]);
        await Task.WhenAll(older, newer);

        Assert.Equal(["new-folder.json"], page.Editor!.Draft.TemplateChoices);
    }

    [Fact]
    public async Task A_refused_save_says_why_and_keeps_the_edits()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;
        editor.Draft.NegativePrompt = "logo";
        _client.ProfileSaveFailure = new ArgumentException("Each aspect can have only one size.");

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Each aspect can have only one size.", editor.Error);
        Assert.True(editor.IsDirty);
        Assert.Equal("logo", editor.Draft.NegativePrompt);
        Assert.Equal(1, editor.LatestVersion);
    }

    [Fact]
    public async Task Typing_while_a_save_runs_stays_an_unsaved_change()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;
        var gate = new TaskCompletionSource();
        _client.ProfileSaveGate = gate.Task;

        editor.Draft.NegativePrompt = "logo";
        var saving = editor.SaveCommand.ExecuteAsync(null);
        editor.Draft.NegativePrompt = "logo, text";
        gate.SetResult();
        await saving;

        Assert.Equal("logo", (await _client.GetProfileVersionAsync(editor.Id)).Content.NegativePrompt);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task Invalid_numbers_block_saving()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        editor.Draft.Sizes[0].Width = "0";
        Assert.False(editor.SaveCommand.CanExecute(null));
        // Text that is not a number at all must block saving too, not keep the old value quietly.
        editor.Draft.Sizes[0].Width = "13a4";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Draft.Sizes[0].Width = "";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Draft.Sizes[0].Width = "1344";
        editor.Draft.Steps = "0";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Draft.Steps = "abc";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Draft.Steps = "30";
        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(30, (await _client.GetProfileVersionAsync(editor.Id)).Content.Steps);
    }

    [Fact]
    public async Task Empty_steps_means_the_workflows_own_value()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image() with { Steps = 30 });
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        editor.Draft.Steps = "";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null((await _client.GetProfileVersionAsync(editor.Id)).Content.Steps);
    }

    [Fact]
    public async Task New_profile_creates_v1_selects_it_and_keeps_the_list_in_order()
    {
        _client.AddProfile(ProfileKind.Image, "Anime", Image());
        _client.AddProfile(ProfileKind.Image, "Photoreal", Image());
        var page = await LoadedPage();

        page.StartCreateCommand.Execute(null);
        page.NewKind = ProfileKind.Image;
        page.NewName = "Painted";
        await page.CreateCommand.ExecuteAsync(null);

        Assert.False(page.IsCreating);
        Assert.Equal(["Anime", "Painted", "Photoreal"], page.Profiles.Select(p => p.Name));
        Assert.Equal("Painted", page.SelectedProfile!.Name);
        Assert.Equal("Painted · v1", page.SelectedProfile.Label);
        Assert.Equal("{shot.visual}", page.Editor!.Draft.PromptTemplate);
    }

    [Fact]
    public async Task A_duplicate_name_is_refused_and_the_form_stays_open()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();

        page.StartCreateCommand.Execute(null);
        page.NewName = "painted";
        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsCreating);
        Assert.NotNull(page.CreateError);
        Assert.Single(page.Profiles);
    }

    [Fact]
    public async Task Create_needs_a_name()
    {
        var page = await LoadedPage();
        page.StartCreateCommand.Execute(null);

        Assert.False(page.CreateCommand.CanExecute(null));
        page.NewName = "   ";
        Assert.False(page.CreateCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(ProfileKind.Research, true, false, false, false, false)]
    [InlineData(ProfileKind.Script, false, false, false, false, false)]
    [InlineData(ProfileKind.Image, false, true, true, false, false)]
    [InlineData(ProfileKind.Video, false, true, true, true, false)]
    [InlineData(ProfileKind.Voice, false, true, false, false, true)]
    public async Task Each_kind_shows_its_own_fields(ProfileKind kind, bool sources, bool media, bool sizes, bool clip, bool voice)
    {
        _client.AddProfile(kind, "P", FakeStoryForgeClient.EmptyContent);
        var page = await LoadedPage();
        await Open(page, "P");
        var form = page.Editor!.Draft;

        Assert.Equal(sources, form.IsResearch);
        Assert.Equal(media, form.IsMedia);
        Assert.Equal(sizes, form.HasSizes);
        Assert.Equal(clip, form.IsVideo);
        Assert.Equal(voice, form.IsVoice);
    }

    [Fact]
    public async Task Fields_a_kind_does_not_show_are_kept_when_saving()
    {
        // A script profile has no sizes on screen; whatever is stored there must survive a save.
        _client.AddProfile(ProfileKind.Script, "Lore", FakeStoryForgeClient.EmptyContent with { Sizes = [new("1:1", 512, 512)] });
        var page = await LoadedPage();
        await Open(page, "Lore");
        var editor = page.Editor!;

        editor.Draft.Instructions = "Shorter.";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal([new GenerationSize("1:1", 512, 512)], (await _client.GetProfileVersionAsync(editor.Id)).Content.Sizes);
    }

    [Fact]
    public async Task Research_sources_are_one_per_line()
    {
        _client.AddProfile(ProfileKind.Research, "Wiki", FakeStoryForgeClient.EmptyContent with { ResearchSources = ["https://a.org"] });
        var page = await LoadedPage();
        await Open(page, "Wiki");
        var editor = page.Editor!;

        editor.Draft.ResearchSources = "https://a.org\r\n\r\n  https://b.org  \r\n";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["https://a.org", "https://b.org"], (await _client.GetProfileVersionAsync(editor.Id)).Content.ResearchSources);
    }

    [Fact]
    public async Task Added_reference_files_are_imported_once_and_can_be_removed()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var editor = page.Editor!;

        await editor.AddReferenceFilesAsync([@"D:\refs\hero.png", @"D:\refs\hero.png", @"D:\refs\castle.png"]);

        Assert.Equal([@"C:\Data\references\hero.png", @"C:\Data\references\castle.png"], editor.Draft.ReferenceFiles);
        Assert.True(editor.IsDirty);
        editor.RemoveReferenceFileCommand.Execute(@"C:\Data\references\hero.png");
        Assert.Equal([@"C:\Data\references\castle.png"], editor.Draft.ReferenceFiles);
    }

    [Fact]
    public async Task Workflow_templates_are_offered_and_a_missing_saved_one_is_kept()
    {
        _client.WorkflowTemplates.AddRange(["qwen-image.json", "krea-turbo.json"]);
        _client.AddProfile(ProfileKind.Image, "Painted", Image() with { WorkflowTemplate = "old-flux.json" });
        var page = await LoadedPage();
        await Open(page, "Painted");

        Assert.Equal(["old-flux.json", "qwen-image.json", "krea-turbo.json"], page.Editor!.Draft.TemplateChoices);
        Assert.Equal("old-flux.json", page.Editor.Draft.WorkflowTemplate);
    }

    [Fact]
    public async Task Every_failed_import_is_reported_not_just_the_last()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        _client.FailingImports.UnionWith(["a.png", "c.png"]);

        await page.Editor!.AddReferenceFilesAsync([@"D:\a.png", @"D:\b.png", @"D:\c.png"]);

        Assert.Contains("a.png", page.Editor.Error);
        Assert.Contains("c.png", page.Editor.Error);
        Assert.Equal([@"C:\Data\references\b.png"], page.Editor.Draft.ReferenceFiles);
    }

    [Fact]
    public async Task Screen_readers_hear_when_a_profile_has_unsaved_changes()
    {
        _client.AddProfile(ProfileKind.Image, "Painted", Image());
        var page = await LoadedPage();
        await Open(page, "Painted");
        var item = page.Profiles.Single();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Editor!.Draft.NegativePrompt = "logo";

        Assert.Equal("Painted · v1, unsaved changes", item.AccessibleName);
        Assert.Contains(nameof(item.AccessibleName), changed);
    }

    [Fact]
    public async Task A_templates_folder_changed_just_before_opening_the_screen_is_used()
    {
        // Settings saves a moment after the last keystroke; opening Profiles must not read the
        // templates before that save.
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.FromHours(1));
        await main.LoadAsync();
        var settings = (SettingsPageViewModel)main.NavItems.Single(item => item.Title == "Settings").Page;
        settings.ComfyUi.WorkflowTemplatesFolder = @"D:\workflows";

        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Profiles");
        await main.PageShowing;

        Assert.Equal(@"D:\workflows", _client.TemplateReadFolders[^1]);
    }

    [Fact]
    public async Task A_profile_list_that_failed_to_load_is_tried_again_when_the_screen_opens()
    {
        _client.ProfileListFailure = new InvalidOperationException("database is locked");
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        _client.ProfileListFailure = null;
        _client.AddProfile(ProfileKind.Image, "Painted", Image());

        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Profiles");
        await main.PageShowing;

        var page = (ProfilesPageViewModel)main.CurrentPage;
        Assert.Equal(["Painted"], page.Profiles.Select(p => p.Name));
        Assert.Null(page.LoadError);
    }

    [Fact]
    public async Task Opening_the_screen_reads_the_workflow_templates_again()
    {
        var main = new MainViewModel(_client, new ProviderStatusBoard(_client), TimeSpan.Zero);
        await main.LoadAsync();
        var before = _client.TemplateReads;

        main.SelectedNavItem = main.NavItems.Single(item => item.Title == "Profiles");

        Assert.Equal(before + 1, _client.TemplateReads);
    }
}
