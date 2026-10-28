using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;
using static StoryForge.App.Tests.StoryboardMatrixTests;

namespace StoryForge.App.Tests;

/// <summary>
/// Rerun pipeline from here ↓: it asks first with what it would make again, and goes ahead only on a
/// yes, from the cell selected; a cell a rerun waits on says so, with Stop rerun.
/// </summary>
public sealed class RerunTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly List<string> _asked = [];
    private bool _answer = true;

    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static StillView Still(string key) =>
        new(key, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)],
            new Still(key, $@"media\stills\{key.Replace('/', '-')}.png", "Karlach in the crypt", 7, new GenerationSize("16:9", 1344, 768), "qwen.json", ["R01/1"]),
            null, "abc", false, null, null, []);

    private static VoiceSegmentView Voice(string id, StageState state = StageState.Approved, bool rerun = false) =>
        new(id, state, 1, state == StageState.Approved ? 1 : null, [new ResultVersion(1, VersionOrigin.Generated, At, null)], null, 10, [], [], null, []) { Rerun = rerun };

    private async Task<(ResultMatrixPageViewModel Page, Project Project)> OpenAsync(VoiceSegmentView? s02 = null)
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook"), Segment("S02", "Outro")], "", 20, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [], [Voice("S01"), s02 ?? Voice("S02")], 20, 60);
        _client.Storyboards[project.Id] = Planned(project.Id, StageState.Approved,
            Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved),
            Shot("S02", 1, 0, 5, 0, 10, StageState.Approved));
        _client.Stills[project.Id] = new StillsView(project.Id, StageState.Approved, null, [], [Still("S01/0001"), Still("S02/0001")], true);
        _client.RerunPlan = (stage, key) => new RerunPlan($"This remakes {key}'s {stage.ToString().ToLowerInvariant()}.", true);
        var page = new ResultMatrixPageViewModel(_client, _ => { }, pickPicture: () => null, confirm: question =>
        {
            _asked.Add(question);
            return _answer;
        });
        page.Show(project);
        await page.ScriptLoading;
        return (page, project);
    }

    [Fact]
    public async Task Rerun_from_here_asks_first_and_nothing_happens_without_a_yes()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;
        script.Rows[1].Voice.SelectCommand.Execute(null);
        _answer = false;

        await script.RerunFromHereCommand.ExecuteAsync(null);

        Assert.Equal("This remakes S02's voice. Go on?", Assert.Single(_asked));
        Assert.Empty(_client.Reruns);

        _answer = true;
        await script.RerunFromHereCommand.ExecuteAsync(null);
        Assert.Equal(("rerun", PipelineStage.Voice, "S02"), Assert.Single(_client.Reruns));
    }

    [Fact]
    public async Task It_reruns_from_the_cell_selected()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;
        var shot = script.Rows[0].Shots[0];

        script.Rows[0].SelectCommand.Execute(null);
        await script.RerunFromHereCommand.ExecuteAsync(null);
        shot.SelectCommand.Execute(null);
        await script.RerunFromHereCommand.ExecuteAsync(null);
        shot.Still.SelectCommand.Execute(null);
        await script.RerunFromHereCommand.ExecuteAsync(null);

        Assert.Equal(
            [("rerun", PipelineStage.Script, "S01"), ("rerun", PipelineStage.Storyboard, shot.Key), ("rerun", PipelineStage.Stills, shot.Key)],
            _client.Reruns);
    }

    [Fact]
    public async Task A_cell_a_rerun_waits_on_says_so_and_Stop_rerun_stops_it()
    {
        var (page, _) = await OpenAsync(Voice("S02", StageState.NeedsReview, rerun: true));
        var script = page.Script!;

        script.Rows[1].Voice.SelectCommand.Execute(null);

        Assert.True(script.SelectedIsRerun);
        Assert.Equal("Part of a rerun: once you approve it, what is made from it is made again.", script.SelectedRerunText);
        script.Rows[0].Voice.SelectCommand.Execute(null);
        Assert.False(script.SelectedIsRerun);

        await script.StopRerunCommand.ExecuteAsync(null);
        Assert.Equal(("stop", default(PipelineStage), ""), Assert.Single(_client.Reruns));
    }

    [Fact]
    public async Task With_nothing_to_make_it_says_why_and_asks_nothing()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;
        script.Rows[1].Voice.SelectCommand.Execute(null);
        _client.RerunPlan = (_, _) => new RerunPlan("Nothing is made from S02's voice yet.", false);

        await script.RerunFromHereCommand.ExecuteAsync(null);

        Assert.Empty(_asked);
        Assert.Empty(_client.Reruns);
        Assert.Equal("Nothing is made from S02's voice yet.", script.ActionError);
    }

    [Fact]
    public async Task A_cell_being_made_cannot_be_rerun_from()
    {
        var (page, _) = await OpenAsync(Voice("S02", StageState.Running));
        var script = page.Script!;

        script.Rows[1].Voice.SelectCommand.Execute(null);

        Assert.False(script.RerunFromHereCommand.CanExecute(null));
    }

    [Fact]
    public async Task What_the_engine_refuses_shows_as_the_reason()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;
        script.Rows[1].Voice.SelectCommand.Execute(null);
        _client.RerunPlan = (_, _) => throw new KeyNotFoundException("There is nothing made for S02 yet.");

        await script.RerunFromHereCommand.ExecuteAsync(null);

        Assert.Equal("Could not rerun the pipeline from here: There is nothing made for S02 yet.", script.ActionError);
        Assert.Empty(_asked);
    }

    private async Task<ResultMatrixPageViewModel> ReferencesAsync(int stillsUsingImage, bool rerun = false)
    {
        var (page, project) = await OpenAsync();
        var image = new ReferenceImageView("R01/1", 1, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)],
            new ReferenceImage("R01", 1, "references/R01-1.png", ["P01"], 1, ImageMaking.FromPicture, ""), null, null, []) { Rerun = rerun };
        _client.References[project.Id] = new ReferencesView(project.Id, StageState.Approved, null, [], ReferencePhase.Images,
            [new(new Reference("R01", "Karlach", CastKind.Character, "", "Red skin, horns.", ["S01/0001"], ["P01"]), [image])],
            [], [], false, new Dictionary<string, string>(), 2)
        {
            StillsUsing = new Dictionary<string, int> { ["R01"] = stillsUsingImage },
            StillsUsingImage = new Dictionary<string, int> { ["R01/1"] = stillsUsingImage },
        };
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        page.References!.Characters[0].SelectCommand.Execute(null);
        return page;
    }

    [Fact]
    public async Task From_a_reference_image_the_stills_that_use_it_are_made_again_after_a_yes()
    {
        var page = await ReferencesAsync(5);

        await page.References!.RerunFromHereCommand.ExecuteAsync(null);

        Assert.Equal("This remakes R01/1's references. Go on?", Assert.Single(_asked));
        Assert.Equal(("rerun", PipelineStage.References, "R01/1"), Assert.Single(_client.Reruns));
    }

    [Fact]
    public async Task An_image_no_still_uses_has_nothing_to_rerun()
    {
        var page = await ReferencesAsync(0);

        Assert.False(page.References!.RerunFromHereCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_image_a_rerun_waits_on_says_so()
    {
        var page = await ReferencesAsync(5, rerun: true);

        Assert.Equal("Part of a rerun: the stills that use it are made again next.", page.References!.RerunText);
    }
}
