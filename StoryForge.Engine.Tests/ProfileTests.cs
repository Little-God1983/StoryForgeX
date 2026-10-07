using Microsoft.Data.Sqlite;
using StoryForge.Client;

namespace StoryForge.Engine.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly EngineTestHost _engine = new();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task A_fresh_engine_has_no_profiles()
    {
        var client = await _engine.StartClientAsync();

        Assert.Empty(await client.GetProfilesAsync());
    }

    [Fact]
    public async Task A_new_profile_is_saved_as_v1_with_the_starting_content_for_its_kind()
    {
        var client = await _engine.StartClientAsync();

        var created = await client.CreateProfileAsync(ProfileKind.Image, "Painted dark fantasy");
        var v1 = await client.GetProfileVersionAsync(created.Id);

        Assert.Equal(new ProfileSummary(created.Id, ProfileKind.Image, "Painted dark fantasy", 1), created);
        Assert.Equal(1, v1.Version);
        Assert.Equal("ComfyUI", v1.Content.Provider);
        Assert.Contains(new GenerationSize("16:9", 1344, 768), v1.Content.Sizes);
        Assert.Contains(new GenerationSize("9:16", 768, 1344), v1.Content.Sizes);
        Assert.Equal(["prompt", "negative", "width", "height", "seed", "steps"], v1.Content.Inputs.Select(i => i.Key));
    }

    [Theory]
    [InlineData(ProfileKind.Video, 20)]
    [InlineData(ProfileKind.Image, null)]
    [InlineData(ProfileKind.Voice, null)]
    public async Task Only_video_profiles_start_with_a_max_clip_length(ProfileKind kind, int? expected)
    {
        var client = await _engine.StartClientAsync();

        var created = await client.CreateProfileAsync(kind, "New");

        Assert.Equal(expected, (await client.GetProfileVersionAsync(created.Id)).Content.MaxClipSeconds);
    }

    [Theory]
    [InlineData(ProfileKind.Research)]
    [InlineData(ProfileKind.Script)]
    [InlineData(ProfileKind.Storyboard)]
    public async Task Text_profiles_start_without_workflow_or_sizes(ProfileKind kind)
    {
        var client = await _engine.StartClientAsync();

        var created = await client.CreateProfileAsync(kind, "New");
        var content = (await client.GetProfileVersionAsync(created.Id)).Content;

        Assert.Equal("Claude CLI", content.Provider);
        Assert.NotEmpty(content.Instructions);
        Assert.Empty(content.Inputs);
        Assert.Empty(content.Sizes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("painted dark fantasy ")]   // same name as an existing one, apart from case and spaces
    public async Task Empty_or_duplicate_names_are_rejected(string name)
    {
        var client = await _engine.StartClientAsync();
        await client.CreateProfileAsync(ProfileKind.Image, "Painted dark fantasy");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => client.CreateProfileAsync(ProfileKind.Image, name));

        Assert.Single(await client.GetProfilesAsync());
        // The app shows the message as it is; .NET's "(Parameter 'name')" suffix must not be in it.
        Assert.DoesNotContain("Parameter", refused.Message);
    }

    [Fact]
    public async Task The_same_name_is_fine_for_another_kind()
    {
        var client = await _engine.StartClientAsync();
        await client.CreateProfileAsync(ProfileKind.Image, "Dark fantasy");

        await client.CreateProfileAsync(ProfileKind.Video, "Dark fantasy");

        Assert.Equal(2, (await client.GetProfilesAsync()).Count);
    }

    [Fact]
    public async Task Names_are_trimmed()
    {
        var client = await _engine.StartClientAsync();

        var created = await client.CreateProfileAsync(ProfileKind.Script, "  Documentary  ");

        Assert.Equal("Documentary", created.Name);
    }

    [Fact]
    public async Task Profiles_are_listed_by_kind_then_name()
    {
        var client = await _engine.StartClientAsync();
        await client.CreateProfileAsync(ProfileKind.Voice, "Narrator");
        await client.CreateProfileAsync(ProfileKind.Image, "b-roll");
        await client.CreateProfileAsync(ProfileKind.Research, "Wikipedia first");
        await client.CreateProfileAsync(ProfileKind.Image, "Anime");

        var list = await client.GetProfilesAsync();

        Assert.Equal(["Wikipedia first", "Anime", "b-roll", "Narrator"], list.Select(p => p.Name));
    }

    [Fact]
    public async Task Saving_adds_a_version_and_keeps_the_old_one()
    {
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Image, "Painted");
        var v1 = await client.GetProfileVersionAsync(created.Id);

        var v2 = await client.SaveProfileVersionAsync(created.Id, v1.Content with { PromptTemplate = "{shot.visual}, oil paint" });

        Assert.Equal(2, v2.Version);
        Assert.Equal("{shot.visual}, oil paint", (await client.GetProfileVersionAsync(created.Id)).Content.PromptTemplate);
        Assert.Equal(v1.Content.PromptTemplate, (await client.GetProfileVersionAsync(created.Id, 1)).Content.PromptTemplate);
        Assert.Equal(2, (await client.GetProfilesAsync()).Single().LatestVersion);
    }

    [Fact]
    public async Task Saving_unchanged_content_adds_no_version()
    {
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Image, "Painted");
        var v1 = await client.GetProfileVersionAsync(created.Id);

        // A copy with equal lists, not the same instances: records compare lists by reference.
        var copy = v1.Content with { Sizes = [.. v1.Content.Sizes], Inputs = [.. v1.Content.Inputs] };
        var saved = await client.SaveProfileVersionAsync(created.Id, copy);

        Assert.Equal(1, saved.Version);
        Assert.Equal(1, (await client.GetProfilesAsync()).Single().LatestVersion);
    }

    [Fact]
    public async Task Every_field_survives_a_restart()
    {
        var first = await _engine.StartClientAsync();
        var created = await first.CreateProfileAsync(ProfileKind.Video, "Minimax H3");
        var content = new ProfileContent(
            Instructions: "Describe motion, not stills.",
            ResearchSources: ["https://en.wikipedia.org"],
            Provider: "ComfyUI",
            WorkflowTemplate: "minimax-h3.json",
            PromptTemplate: "{shot.visual}, {shot.motion}",
            NegativePrompt: "blurry",
            Inputs: [new WorkflowInput("prompt", "#6.text"), new WorkflowInput("seed", "#3.seed")],
            Steps: 24,
            Sizes: [new GenerationSize("16:9", 1280, 720)],
            ReferenceFiles: [@"C:\refs\a.png"],
            MaxClipSeconds: 30,
            Voice: "");
        await first.SaveProfileVersionAsync(created.Id, content);

        var second = await _engine.StartClientAsync();
        var loaded = (await second.GetProfileVersionAsync(created.Id)).Content;

        Assert.Equal(content.Instructions, loaded.Instructions);
        Assert.Equal(content.ResearchSources, loaded.ResearchSources);
        Assert.Equal(content.WorkflowTemplate, loaded.WorkflowTemplate);
        Assert.Equal(content.PromptTemplate, loaded.PromptTemplate);
        Assert.Equal(content.NegativePrompt, loaded.NegativePrompt);
        Assert.Equal(content.Inputs, loaded.Inputs);
        Assert.Equal(content.Steps, loaded.Steps);
        Assert.Equal(content.Sizes, loaded.Sizes);
        Assert.Equal(content.ReferenceFiles, loaded.ReferenceFiles);
        Assert.Equal(content.MaxClipSeconds, loaded.MaxClipSeconds);
    }

    [Fact]
    public async Task Saves_running_at_the_same_time_each_get_their_own_version()
    {
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Script, "Documentary");
        var v1 = await client.GetProfileVersionAsync(created.Id);

        var saved = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            client.SaveProfileVersionAsync(created.Id, v1.Content with { Instructions = $"take {i}" })));

        Assert.Equal([2, 3, 4, 5, 6, 7], saved.Select(v => v.Version).Order());
    }

    [Theory]
    [InlineData("width")]
    [InlineData("steps")]
    [InlineData("clip")]
    [InlineData("aspect")]
    public async Task Impossible_numbers_are_rejected_and_nothing_is_saved(string field)
    {
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Video, "Minimax H3");
        var c = (await client.GetProfileVersionAsync(created.Id)).Content;
        var bad = field switch
        {
            "width" => c with { Sizes = [new GenerationSize("16:9", 0, 720)] },
            "steps" => c with { Steps = 0 },
            "clip" => c with { MaxClipSeconds = 0 },
            _ => c with { Sizes = [new GenerationSize("16:9", 1280, 720), new GenerationSize("16:9", 1920, 1080)] },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SaveProfileVersionAsync(created.Id, bad));

        Assert.Equal(1, (await client.GetProfilesAsync()).Single().LatestVersion);
    }

    [Fact]
    public async Task Asking_for_a_profile_or_version_that_does_not_exist_says_so()
    {
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Script, "Documentary");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetProfileVersionAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetProfileVersionAsync(created.Id, 2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.SaveProfileVersionAsync(Guid.NewGuid(), ProfileContent.Empty));
    }

    [Fact]
    public async Task A_version_row_with_missing_fields_reads_them_as_empty()
    {
        // A field added in a later version of the app is missing from rows saved before it.
        var client = await _engine.StartClientAsync();
        var created = await client.CreateProfileAsync(ProfileKind.Script, "Documentary");
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_engine.DataDirectory, "storyforge.db")}"))
        {
            await connection.OpenAsync();
            var update = connection.CreateCommand();
            update.CommandText = """UPDATE ProfileVersions SET Json = '{"Instructions":"Short."}'""";
            await update.ExecuteNonQueryAsync();
        }

        var content = (await client.GetProfileVersionAsync(created.Id)).Content;

        Assert.Equal("Short.", content.Instructions);
        Assert.Empty(content.Sizes);
        Assert.Empty(content.ReferenceFiles);
        Assert.Equal("", content.Voice);
    }

    [Fact]
    public async Task Imported_reference_files_are_copied_into_the_data_folder()
    {
        var client = await _engine.StartClientAsync();
        var source = Path.Combine(_engine.DataDirectory, "outside.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var stored = await client.ImportReferenceFileAsync(source);
        File.Delete(source);

        Assert.StartsWith(Path.Combine(_engine.DataDirectory, "references"), stored);
        Assert.EndsWith(".png", stored);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(stored));
    }

    [Fact]
    public async Task Importing_the_same_file_twice_stores_it_once()
    {
        var client = await _engine.StartClientAsync();
        var source = Path.Combine(_engine.DataDirectory, "outside.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var first = await client.ImportReferenceFileAsync(source);
        var second = await client.ImportReferenceFileAsync(source);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(Path.Combine(_engine.DataDirectory, "references")));
    }

    [Fact]
    public async Task Importing_a_missing_file_fails()
    {
        var client = await _engine.StartClientAsync();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            client.ImportReferenceFileAsync(Path.Combine(_engine.DataDirectory, "nope.png")));
    }

    [Fact]
    public async Task Workflow_templates_are_the_json_files_in_the_templates_folder()
    {
        var client = await _engine.StartClientAsync();
        var folder = Path.Combine(_engine.DataDirectory, "workflows");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "qwen-image.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "breeze-tts.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "");
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = folder } });

        Assert.Equal(["breeze-tts.json", "qwen-image.json"], await client.GetWorkflowTemplatesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"Z:\does\not\exist")]
    public async Task No_templates_folder_means_no_templates(string folder)
    {
        var client = await _engine.StartClientAsync();
        var settings = await client.GetSettingsAsync();
        await client.SaveSettingsAsync(settings with { ComfyUi = settings.ComfyUi with { WorkflowTemplatesFolder = folder } });

        Assert.Empty(await client.GetWorkflowTemplatesAsync());
    }
}
