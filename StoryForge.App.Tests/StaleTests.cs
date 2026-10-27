using StoryForge.App.ViewModels.Pages;
using StoryForge.Client;
using static StoryForge.App.Tests.StoryboardMatrixTests;

namespace StoryForge.App.Tests;

/// <summary>
/// Out-of-date results: a stale cell shows purple ("stale") with its old version and says why in the
/// panel; the References screen asks before a change that puts stills out of date.
/// </summary>
public sealed class StaleTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly List<string> _asked = [];
    private bool _answer = true;

    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static StillView Still(string key, string? stale = null) =>
        new(key, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)],
            new Still(key, $@"media\stills\{key.Replace('/', '-')}.png", "Karlach in the crypt", 7, new GenerationSize("16:9", 1344, 768), "qwen.json", ["R01/1"]),
            null, "abc", false, null, null, []) { Stale = stale };

    private async Task<(ResultMatrixPageViewModel Page, Project Project)> OpenAsync()
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook"), Segment("S02", "Outro")], "", 20, 60, []);
        _client.Voices[project.Id] = new VoiceView(project.Id, StageState.Approved, null, [],
            [
                new VoiceSegmentView("S01", StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)], null, 10, [], [], null, []),
                new VoiceSegmentView("S02", StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)], null, 10, [], [], null, [])
                {
                    Stale = "S02's script changed since it was spoken.",
                },
            ], 20, 60);
        _client.Storyboards[project.Id] = Planned(project.Id, StageState.Approved,
            Shot("S01", 1, 0, 4.2, 0, 4, StageState.Approved),
            Shot("S02", 1, 0, 5, 0, 10, StageState.Approved) with { Stale = "S02's voice is out of date." });
        _client.Stills[project.Id] = new StillsView(project.Id, StageState.Approved, null, [], [Still("S01/0001"), Still("S02/0001", "Shot S02·1 is out of date.")], true);
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
    public async Task Stale_cells_show_stale_and_the_rest_as_they_are()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;

        Assert.Equal(["approved", "stale"], script.Rows.Select(r => r.Voice.StateText));
        Assert.Equal(["approved", "stale"], script.Rows.SelectMany(r => r.Shots).Select(s => s.StateText));
        Assert.Equal(["approved", "stale"], script.Rows.SelectMany(r => r.Shots).Select(s => s.Still.StateText));
        Assert.Equal(StageState.Stale, script.Rows[1].Shots[0].Still.State);
        Assert.Equal("S02·1 still: stale, version 1", script.Rows[1].Shots[0].Still.AccessibleName);   // it keeps its old version
    }

    [Fact]
    public async Task The_panel_says_why_a_cell_is_out_of_date()
    {
        var (page, _) = await OpenAsync();
        var script = page.Script!;

        script.Rows[1].Shots[0].Still.SelectCommand.Execute(null);
        Assert.Equal("Out of date: Shot S02·1 is out of date.", script.SelectedStale);
        Assert.Equal("stale · version 1", script.SelectedStatus);
        Assert.True(script.RegenerateSegmentCommand.CanExecute(null));   // made again the way it is today

        script.Rows[1].Voice.SelectCommand.Execute(null);
        Assert.Equal("Out of date: S02's script changed since it was spoken.", script.SelectedStale);

        script.Rows[0].Shots[0].Still.SelectCommand.Execute(null);
        Assert.Null(script.SelectedStale);
    }

    [Fact]
    public async Task A_folded_segment_counts_its_stale_shots_and_stills()
    {
        var (page, _) = await OpenAsync();
        var row = page.Script!.Rows[1];

        row.ToggleCommand.Execute(null);

        Assert.Equal("~ 1", row.BoardSummary);
        Assert.Equal("~ 1", row.StillSummary);
    }

    private async Task<(ResultMatrixPageViewModel Page, Guid ProjectId)> ReferencesAsync(int stillsUsingKarlach)
    {
        var (page, project) = await OpenAsync();
        var image = new ReferenceImageView("R01/1", 1, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)],
            new ReferenceImage("R01", 1, "references/R01-1.png", ["P01"], 1, ImageMaking.FromPicture, ""), null, null, []);
        _client.References[project.Id] = new ReferencesView(project.Id, StageState.Approved, null, [], ReferencePhase.Images,
            [new(new Reference("R01", "Karlach", CastKind.Character, "", "Red skin, horns.", ["S01/0001"], ["P01"]), [image])],
            [], [], false, new Dictionary<string, string>(), 2)
        {
            StillsUsing = new Dictionary<string, int> { ["R01"] = stillsUsingKarlach },
        };
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        page.References!.Characters[0].SelectCommand.Execute(null);
        return (page, project.Id);
    }

    [Fact]
    public async Task Before_a_reference_that_stills_use_is_made_again_the_screen_asks()
    {
        var (page, _) = await ReferencesAsync(6);
        _answer = false;

        await page.References!.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal("6 stills use Karlach and would be out of date. Go on?", Assert.Single(_asked));
        Assert.Empty(_client.ShotActions);   // nothing happens without a yes

        _answer = true;
        await page.References.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal(("regenerate", "R01/1"), Assert.Single(_client.ShotActions));
    }

    [Fact]
    public async Task A_reference_no_still_uses_changes_without_a_question()
    {
        var (page, _) = await ReferencesAsync(0);

        await page.References!.RegenerateCommand.ExecuteAsync(null);

        Assert.Empty(_asked);
        Assert.Single(_client.ShotActions);
    }

    [Fact]
    public async Task A_stale_reference_image_says_so_on_its_card()
    {
        var (page, project) = await OpenAsync();
        var image = new ReferenceImageView("R01/1", 1, StageState.Approved, 1, 1, [new ResultVersion(1, VersionOrigin.Generated, At, null)],
            new ReferenceImage("R01", 1, "references/R01-1.png", ["P01"], 1, ImageMaking.FromPicture, ""), null, null, [])
        {
            Stale = "Karlach's look or pictures changed since this image was made.",
        };
        _client.References[project.Id] = new ReferencesView(project.Id, StageState.Approved, null, [], ReferencePhase.Images,
            [new(new Reference("R01", "Karlach", CastKind.Character, "", "Red skin, horns.", ["S01/0001"], ["P01"]), [image])],
            [], [], false, new Dictionary<string, string>(), 2);

        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));

        var card = page.References!.Characters[0];
        Assert.Equal(("stale", true), (card.StateText, card.IsStale));
        Assert.Equal("Out of date: Karlach's look or pictures changed since this image was made.", card.Note);
    }
}
