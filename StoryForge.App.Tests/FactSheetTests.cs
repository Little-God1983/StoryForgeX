using StoryForge.App.ViewModels.Pages;
using StoryForge.App.ViewModels.Research;
using StoryForge.Client;

namespace StoryForge.App.Tests;

/// <summary>The Research stage on screen: the live log, the fact sheet with weights and left-out facts, approving.</summary>
public sealed class FactSheetTests
{
    private readonly FakeStoryForgeClient _client = new();
    private readonly List<string> _opened = [];

    private static readonly DateTimeOffset At = new(2026, 10, 7, 15, 40, 0, TimeSpan.Zero);

    private async Task<(ResultMatrixPageViewModel Matrix, Project Project)> StartedAsync()
    {
        var profile = new ProfileRef(Guid.NewGuid(), 1);
        var project = await _client.CreateProjectAsync(new ProjectSetup(
            "Soul Coins – BG3 lore",
            "Soul coins in BG3.",
            ["bg3.wiki", "forgottenrealms.fandom.com"],
            new WritingSetup("Claude CLI", "", profile, profile, profile),
            new VoiceSetup("ComfyUI", "Breeze TTS", profile),
            new StillsSetup("ComfyUI", "Qwen Image 2.1", profile, Consistency.ReferenceImages, new GenerationSize("16:9", 1344, 768)),
            new ClipsSetup("ComfyUI", "Minimax H3", profile, 20, new GenerationSize("16:9", 1280, 720)),
            new OutputSetup("16:9", 1920, 1080, 240, "English", AssemblyTarget.FfmpegWithFcpxml),
            [PipelineStage.Research],
            RunMode.StopAtGates));
        var matrix = new ResultMatrixPageViewModel(_client, _opened.Add);
        await matrix.StartRunAsync(project);
        return (matrix, project);
    }

    private static Fact Fact(string id, string statement, int weight = 5, bool leftOut = false) =>
        new(id, statement, "https://bg3.wiki/wiki/Soul_Coin", "a single mortal soul is bound", weight, leftOut);

    /// <summary>The research finished: v1 waits for review.</summary>
    private async Task FinishAsync(ResultMatrixPageViewModel matrix, Project project, params Fact[] facts)
    {
        _client.FactSheets[project.Id] = new FactSheetView(
            project.Id, StageState.NeedsReview, null,
            [new ResultVersion(1, VersionOrigin.Generated, At, null)], 1, null,
            new FactSheet(facts.Length > 0 ? facts : [Fact("F01", "Coins hold souls."), Fact("F02", "Coins are money in Hell.", 10), Fact("F03", "War machines burn coins.", leftOut: true)]),
            [new ActivityLine(At, ActivityKind.Search, "bg3.wiki  \"soul coin\"  → 6 hits"), new ActivityLine(At, ActivityKind.Fetch, "bg3.wiki/wiki/Soul_Coin")]);
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.NeedsReview));
        await matrix.Updating;
    }

    [Fact]
    public async Task Starting_a_run_opens_the_fact_sheet_and_starts_research()
    {
        var (matrix, project) = await StartedAsync();

        Assert.Equal([project.Id], _client.StartedRuns);
        Assert.True(matrix.ShowsFactSheet);
        Assert.False(matrix.ShowsMatrix);
        Assert.Equal("Projects / Soul Coins – BG3 lore / Research", matrix.Breadcrumb);
        Assert.Equal("bg3.wiki and forgottenrealms.fandom.com", matrix.FactSheet!.SourcesText);
    }

    [Fact]
    public async Task While_research_runs_its_log_fills_in_and_the_chip_says_running()
    {
        var (matrix, project) = await StartedAsync();

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.Running));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.Running, new ActivityLine(At, ActivityKind.Search, "bg3.wiki  \"soul coin\"  → 6 hits")));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.Running, new ActivityLine(At, ActivityKind.Fetch, "bg3.wiki/wiki/Soul_Coin")));
        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.Running, new ActivityLine(At, ActivityKind.Refused, "www.reddit.com/r/BaldursGate3/  (not a project source)")));
        await matrix.Updating;

        var sheet = matrix.FactSheet!;
        Assert.True(sheet.IsRunning);
        Assert.True(sheet.ShowsLog);
        Assert.Equal(3, sheet.Activity.Count);
        Assert.Equal(["bg3.wiki · 1 search · 1 page"], sheet.SiteSummary);
        Assert.Equal("1 request refused (not a project source)", sheet.RefusedSummary);
        Assert.Equal("Research – running", matrix.Stages[0].Label);
        Assert.True(sheet.CancelCommand.CanExecute(null));

        await sheet.CancelCommand.ExecuteAsync(null);
        Assert.Equal([(project.Id, PipelineStage.Research)], _client.Cancelled);
    }

    [Fact]
    public async Task A_finished_research_shows_the_facts_in_use_and_the_left_out_ones_apart()
    {
        var (matrix, project) = await StartedAsync();

        await FinishAsync(matrix, project);

        var sheet = matrix.FactSheet!;
        Assert.True(sheet.IsReview);
        Assert.True(sheet.ShowsSheet);
        Assert.Equal(["F01", "F02"], sheet.Facts.Select(f => f.Id));
        Assert.Equal(["F03"], sheet.LeftOutFacts.Select(f => f.Id));
        Assert.Equal("2 facts in use, 1 left out, 1 marked must.", sheet.Summary);
        Assert.Equal("F01", sheet.SelectedFact!.Id);
        Assert.True(sheet.Facts[1].IsMust);
        Assert.Equal("must be in the video", sheet.Facts[1].WeightMeaning);
        Assert.Equal("bg3.wiki/wiki/Soul_Coin", sheet.Facts[0].SourceDisplay);
        sheet.Facts[0].Update(Fact("F01", "x") with { SourceUrl = "https://bg3.wiki/wiki/Soul_Coins%3A_A_Treatise" });
        Assert.Equal("bg3.wiki/wiki/Soul_Coins:_A_Treatise", sheet.Facts[0].SourceDisplay);
        Assert.Equal([new VersionChoice(1, IsShown: true, IsApproved: false)], sheet.Versions);
        Assert.Equal("Research – review", matrix.Stages[0].Label);
    }

    [Fact]
    public async Task Plus_and_minus_change_the_weight_from_1_to_10()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project, Fact("F01", "Coins hold souls.", weight: 9), Fact("F02", "Coins are money.", weight: 2));
        var sheet = matrix.FactSheet!;

        await sheet.Facts[0].RaiseWeightCommand.ExecuteAsync(null);
        await sheet.Facts[1].LowerWeightCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, 1, "F01", new FactChange(Weight: 10)), (project.Id, 1, "F02", new FactChange(Weight: 1))], _client.FactChanges);
        Assert.Equal(10, sheet.Facts[0].Weight);
        Assert.Equal(1, sheet.Facts[1].Weight);
        Assert.Equal("only if there is time left", sheet.Facts[1].WeightMeaning);
        Assert.False(sheet.Facts[0].RaiseWeightCommand.CanExecute(null));
        Assert.False(sheet.Facts[1].LowerWeightCommand.CanExecute(null));
    }

    [Fact]
    public async Task Leave_out_moves_a_fact_to_the_second_list_and_Put_back_brings_it_back()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;
        var first = sheet.Facts[0];

        await first.LeaveOutCommand.ExecuteAsync(null);

        Assert.Equal(["F02"], sheet.Facts.Select(f => f.Id));
        Assert.Equal(["F01", "F03"], sheet.LeftOutFacts.Select(f => f.Id));
        Assert.Equal("1 fact in use, 2 left out, 1 marked must.", sheet.Summary);

        await sheet.LeftOutFacts[1].PutBackCommand.ExecuteAsync(null);

        Assert.Equal(["F02", "F03"], sheet.Facts.Select(f => f.Id));
        Assert.Equal(new FactChange(LeftOut: false), _client.FactChanges[^1].Change);
    }

    [Fact]
    public async Task A_fact_is_reworded_in_the_panel()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;
        sheet.Facts[1].SelectCommand.Execute(null);

        sheet.StartEditCommand.Execute(null);
        Assert.Equal("Coins are money in Hell.", sheet.EditText);
        sheet.EditText = "  ";
        Assert.False(sheet.SaveEditCommand.CanExecute(null));
        sheet.EditText = "Soul coins are the money of the Nine Hells.";
        await sheet.SaveEditCommand.ExecuteAsync(null);

        Assert.False(sheet.IsEditing);
        Assert.Equal((project.Id, 1, "F02", new FactChange(Statement: "Soul coins are the money of the Nine Hells.")), _client.FactChanges.Single());
        Assert.Equal("Soul coins are the money of the Nine Hells.", sheet.SelectedFact!.Statement);
    }

    [Fact]
    public async Task Cancel_leaves_the_wording_as_it_was()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;

        sheet.StartEditCommand.Execute(null);
        sheet.EditText = "Something else.";
        sheet.CancelEditCommand.Execute(null);

        Assert.False(sheet.IsEditing);
        Assert.Empty(_client.FactChanges);
        Assert.Equal("Coins hold souls.", sheet.SelectedFact!.Statement);
    }

    [Fact]
    public async Task Approve_approves_the_version_on_screen()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;

        await sheet.ApproveCommand.ExecuteAsync(null);

        Assert.Equal([(project.Id, PipelineStage.Research, 1)], _client.Approved);
        Assert.True(sheet.IsApproved);
        Assert.False(sheet.ApproveCommand.CanExecute(null));
        Assert.Equal("v1 · researched 07 Oct " + At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " · approved", sheet.VersionNote);
    }

    [Fact]
    public async Task An_approve_that_does_not_go_through_keeps_saying_why()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;
        _client.StageFailure = new InvalidOperationException("The Research stage is running. Approve it when it is done.");

        await sheet.ApproveCommand.ExecuteAsync(null);

        Assert.Equal("Could not approve the fact sheet: The Research stage is running. Approve it when it is done.", sheet.ActionError);
        Assert.True(sheet.IsReview);
    }

    [Fact]
    public async Task Opening_a_project_on_the_empty_matrix_screen_shows_its_matrix()
    {
        var (_, project) = await StartedAsync();
        var empty = new ResultMatrixPageViewModel(_client, _opened.Add);
        var changed = new List<string?>();
        empty.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await empty.OpenAsync(project.Id);

        Assert.True(empty.ShowsMatrix);
        Assert.Contains(nameof(ResultMatrixPageViewModel.ShowsMatrix), changed);
    }

    [Fact]
    public async Task An_older_version_can_be_shown()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        _client.SheetVersions[(project.Id, 1)] = new FactSheet([Fact("F01", "The old wording.")]);
        var sheet = matrix.FactSheet!;

        await sheet.ShowVersionCommand.ExecuteAsync(1);

        Assert.Equal(["F01"], sheet.Facts.Select(f => f.Id));
        Assert.Equal("The old wording.", sheet.Facts[0].Statement);
    }

    [Fact]
    public async Task A_failed_research_shows_why_and_Retry_runs_it_again()
    {
        var (matrix, project) = await StartedAsync();
        _client.FactSheets[project.Id] = new FactSheetView(project.Id, StageState.Failed,
            "The answer was not a valid fact sheet after 3 tries. Last problem: F04 has no source link.", [], null, null, null,
            [new ActivityLine(At, ActivityKind.Check, "try 3: F04 has no source link. – stage failed")]);

        _client.Raise(new StageUpdate(project.Id, PipelineStage.Research, StageState.Failed));
        await matrix.Updating;

        var sheet = matrix.FactSheet!;
        Assert.True(sheet.IsFailed);
        Assert.True(sheet.ShowsLog);
        Assert.StartsWith("The answer was not a valid fact sheet", sheet.Error);
        Assert.Single(sheet.Activity);
        Assert.Equal("Research – failed", matrix.Stages[0].Label);

        await sheet.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal([(project.Id, PipelineStage.Research)], _client.Regenerated);
    }

    [Fact]
    public async Task A_change_that_does_not_go_through_says_why()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);
        var sheet = matrix.FactSheet!;
        _client.StageFailure = new InvalidOperationException("The research is running. Change facts when it is done.");

        await sheet.Facts[0].RaiseWeightCommand.ExecuteAsync(null);

        Assert.Equal("Could not change F01: The research is running. Change facts when it is done.", sheet.ActionError);
        Assert.Equal(5, sheet.Facts[0].Weight);
    }

    [Fact]
    public async Task The_source_link_opens_the_page()
    {
        var (matrix, project) = await StartedAsync();
        await FinishAsync(matrix, project);

        matrix.FactSheet!.OpenSourceCommand.Execute(null);

        Assert.Equal(["https://bg3.wiki/wiki/Soul_Coin"], _opened);
    }

    [Fact]
    public async Task The_Research_chip_opens_the_fact_sheet_and_the_link_goes_back()
    {
        var (matrix, _) = await StartedAsync();
        matrix.ShowMatrixCommand.Execute(null);
        Assert.True(matrix.ShowsMatrix);
        Assert.Equal("Projects / Soul Coins – BG3 lore", matrix.Breadcrumb);

        await matrix.OpenStageCommand.ExecuteAsync(matrix.Stages[0]);

        Assert.True(matrix.ShowsFactSheet);
        Assert.True(matrix.Stages[0].CanOpen);
        Assert.All(matrix.Stages.Skip(1), chip => Assert.False(chip.CanOpen));   // their screens come with their stages
    }

    [Fact]
    public async Task Updates_for_another_project_change_nothing()
    {
        var (matrix, _) = await StartedAsync();

        _client.Raise(new StageUpdate(Guid.NewGuid(), PipelineStage.Research, StageState.Running, new ActivityLine(At, ActivityKind.Fetch, "x")));
        await matrix.Updating;

        Assert.Equal(StageState.NotStarted, matrix.Stages[0].State);
        Assert.Empty(matrix.FactSheet!.Activity);
    }

    [Fact]
    public async Task A_run_that_cannot_start_says_why()
    {
        var (matrix, project) = await StartedAsync();
        _client.StageFailure = new InvalidOperationException("engine down");

        await matrix.StartRunAsync(project);

        Assert.Equal("Could not start the run: engine down", matrix.LoadError);
    }
}
