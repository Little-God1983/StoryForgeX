using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.References;
using StoryForge.Client;
using static StoryForge.App.Tests.StoryboardMatrixTests;

namespace StoryForge.App.Tests;

/// <summary>
/// The References timeline (board 9): a row per reference, a column per shot as wide as it lasts, a
/// bar where the reference is in the shot; a click puts it in or takes it out, and a full shot takes no more.
/// </summary>
public sealed class ReferencesTimelineTests
{
    private readonly FakeStoryForgeClient _client = new();

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string> { ["S01/a"] = "S01·1", ["S01/b"] = "S01·2", ["S02/a"] = "S02·1" };

    private static ReferenceView Card(string id, string name, CastKind kind, params string[] shots) =>
        new(new Reference(id, name, kind, "", "", shots, []), []);

    /// <summary>
    /// Karlach in the first two shots, the House of Hope in the first, the soul coin in the second;
    /// the second is full with a tavern mug and Raphael, who are not references.
    /// </summary>
    private static ReferencesView Timed(Guid projectId) => new(
        projectId, StageState.NeedsReview, null, [], ReferencePhase.List,
        [
            Card("R01", "Karlach", CastKind.Character, "S01/a", "S01/b"),
            Card("R02", "Soul coin", CastKind.Object, "S01/b"),
            Card("R03", "House of Hope", CastKind.Place, "S01/a"),
        ],
        [], [new OtherCast("A tavern mug", CastKind.Object, 1)], false, Labels, 3)
    {
        Shots =
        [
            new("S01/a", "S01·1", 0, 4.2, ["Karlach", "House of Hope"]),
            new("S01/b", "S01·2", 4.2, 6, ["Karlach", "Soul coin", "A tavern mug", "Raphael"]),
            new("S02/a", "S02·1", 10.2, 5, []),
        ],
    };

    private async Task<(ReferencesViewModel Screen, Guid ProjectId)> OpenAsync()
    {
        var project = await _client.CreateProjectAsync(Setup());
        _client.Scripts[project.Id] = new ScriptView(project.Id, StageState.Approved, null, [], [Segment("S01", "Hook")], "", 10, 60, []);
        _client.References[project.Id] = Timed(project.Id);
        var page = new ResultMatrixPageViewModel(_client, _ => { }, pickPicture: () => null);
        page.Show(project);
        await page.ScriptLoading;
        await page.OpenStageCommand.ExecuteAsync(page.Stages.Single(s => s.Stage == PipelineStage.References));
        return (page.References!, project.Id);
    }

    private static TimelineCellViewModel Cell(ReferencesViewModel screen, string name, string shot) =>
        screen.TimelineRows.Single(r => r.Name == name).Cells.Single(c => c.ShotKey == shot);

    [Fact]
    public async Task A_row_per_reference_and_a_column_per_shot_as_wide_as_it_lasts()
    {
        var (screen, _) = await OpenAsync();

        Assert.Equal(["Karlach", "House of Hope", "Soul coin"], screen.TimelineRows.Select(r => r.Name));
        Assert.Equal(["S01·1", "S01·2", "S02·1"], screen.TimelineShots.Select(s => s.Label));
        Assert.Equal([4.2, 6, 5], screen.TimelineShots.Select(s => s.Seconds));
        Assert.Equal(["0:00", "0:04", "0:10"], screen.TimelineShots.Select(s => s.StartText));
        Assert.Equal([true, true, false], screen.TimelineRows[0].Cells.Select(c => c.IsOn));
        Assert.Equal(2, screen.TimelineRows[0].Count);
        Assert.True(screen.IsTimelineOpen);
    }

    [Fact]
    public async Task Under_each_column_a_count_out_of_4_and_the_summary_names_the_full_shots()
    {
        var (screen, _) = await OpenAsync();

        Assert.Equal(["2/4", "4/4", "0/4"], screen.TimelineShots.Select(s => s.CountText));
        Assert.Equal([false, true, false], screen.TimelineShots.Select(s => s.IsFull));
        Assert.Equal("3 shots · 0:15 · S01·2 full (4/4)", screen.TimelineSummary);
    }

    [Fact]
    public async Task A_click_puts_the_reference_in_the_shot_or_takes_it_out()
    {
        var (screen, _) = await OpenAsync();

        await screen.ToggleCellCommand.ExecuteAsync(Cell(screen, "Karlach", "S02/a"));
        await screen.ToggleCellCommand.ExecuteAsync(Cell(screen, "Karlach", "S01/a"));

        Assert.Equal([("R01", "S02/a", true), ("R01", "S01/a", false)], _client.TimelineChanges);
    }

    [Fact]
    public async Task A_full_shot_takes_no_more()
    {
        var (screen, _) = await OpenAsync();

        var cell = Cell(screen, "House of Hope", "S01/b");

        Assert.True(cell.IsBlocked);
        Assert.False(screen.ToggleCellCommand.CanExecute(cell));
        Assert.Equal("House of Hope in S01·2: the shot is full, 4 of 4", cell.AccessibleName);
        Assert.True(screen.ToggleCellCommand.CanExecute(Cell(screen, "Soul coin", "S01/b")));   // taking one out still works
        Assert.Equal("Soul coin in S01·2: used, click to remove", Cell(screen, "Soul coin", "S01/b").AccessibleName);
        Assert.Equal("Soul coin in S01·1: not used, click to add", Cell(screen, "Soul coin", "S01/a").AccessibleName);
    }

    [Fact]
    public async Task Picking_a_reference_lights_up_the_shots_it_is_in()
    {
        var (screen, _) = await OpenAsync();

        screen.TimelineRows[1].PickCommand.Execute(null);

        Assert.Equal("R03", screen.SelectedCard?.Id);
        Assert.Equal([true, false, false], screen.TimelineShots.Select(s => s.IsLit));
        Assert.Equal([false, true, false], screen.TimelineRows.Select(r => r.IsSelected));
        Assert.Equal([true, false, true], screen.TimelineRows.Select(r => r.IsDimmed));

        screen.Places[0].SelectCommand.Execute(null);   // the card does the same
        Assert.True(screen.TimelineRows[1].IsSelected);
    }

    [Fact]
    public async Task The_timeline_folds_away()
    {
        var (screen, _) = await OpenAsync();

        screen.ToggleTimelineCommand.Execute(null);

        Assert.False(screen.IsTimelineOpen);
    }

    [Fact]
    public async Task A_change_to_a_shot_elsewhere_shows_on_the_timeline()
    {
        var (screen, projectId) = await OpenAsync();
        var view = Timed(projectId);
        _client.References[projectId] = view with
        {
            References = [.. view.References.Select(r => r.Reference.Id == "R02" ? r with { Reference = r.Reference with { Shots = ["S01/b", "S02/a"] } } : r)],
            Shots = [view.Shots[0], view.Shots[1], view.Shots[2] with { Cast = ["Soul coin"] }],
        };

        await screen.ReceiveAsync(new StageUpdate(projectId, PipelineStage.Storyboard, StageState.Approved, Key: "S02/a"));

        Assert.True(Cell(screen, "Soul coin", "S02/a").IsOn);
        Assert.Equal("1/4", screen.TimelineShots[2].CountText);
    }

    [Fact]
    public async Task A_change_that_is_refused_says_why()
    {
        var (screen, _) = await OpenAsync();
        _client.StageFailure = new InvalidOperationException("The storyboard is being planned. Try again when it is done.");

        await screen.ToggleCellCommand.ExecuteAsync(Cell(screen, "Karlach", "S02/a"));

        Assert.Equal("Could not change the shot: The storyboard is being planned. Try again when it is done.", screen.ActionError);
    }
}
