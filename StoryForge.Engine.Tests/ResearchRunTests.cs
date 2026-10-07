
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Data;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Research stage in the pipeline, with a scripted model in place of Claude CLI: states, gates,
/// retries with the problems sent back, failures, versions, and your changes to facts.
/// </summary>
public sealed class ResearchRunTests : IDisposable
{
    private const string Page = "https://bg3.wiki/wiki/Soul_Coin";
    private const string PageText = "Soul Coins are small, coin-shaped objects forged of infernal iron into which a single mortal soul is bound.";

    private readonly EngineTestHost _engine = new();
    private readonly FakeResearchAgent _agent = new();

    public void Dispose() => _engine.Dispose();

    private Task<IStoryForgeClient> StartAsync() =>
        _engine.StartClientAsync(services => services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(_agent)));

    private static async Task<Project> NewProjectAsync(IStoryForgeClient client, Func<ProjectSetup, ProjectSetup>? change = null)
    {
        var setup = await ProjectTests.ValidSetup(client);
        return await client.CreateProjectAsync(change is null ? setup : change(setup));
    }

    private static async Task<StageState> WaitForAsync(IStoryForgeClient client, Guid projectId, params StageState[] states)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var state = (await client.GetProjectAsync(projectId)).Stages[0].State;
            if (states.Contains(state))
            {
                return state;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Research stayed {state}.");
            }
            await Task.Delay(20);
        }
    }

    private static ResearchOutput Good(params string[] statements) =>
        new([.. (statements.Length == 0 ? ["Coins hold souls."] : statements).Select(s => new ResearchFact(s, Page, "a single mortal soul is bound"))]);

    private static ResearchOutput Invented() =>
        new([new ResearchFact("Mammon mints the coins.", Page, "Mammon mints every coin.")]);

    [Fact]
    public async Task Start_runs_research_and_it_waits_at_its_gate_with_a_fact_sheet()
    {
        _agent.Answer(Good("Coins hold souls.", "Coins are money in Hell."));
        var client = await StartAsync();
        var updates = new List<StageUpdate>();
        client.StageUpdated += (_, update) => { lock (updates) { updates.Add(update); } };
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.NeedsReview, await WaitForAsync(client, project.Id, StageState.NeedsReview, StageState.Failed));
        var view = await client.GetFactSheetAsync(project.Id);
        Assert.Equal(1, view.Version);
        Assert.Null(view.ApprovedVersion);
        Assert.Equal(["F01", "F02"], view.Sheet!.Facts.Select(f => f.Id));
        Assert.All(view.Sheet.Facts, f => Assert.Equal(Fact.DefaultWeight, f.Weight));
        Assert.Contains(view.Activity, a => a.Kind == ActivityKind.Fetch);
        Assert.Contains(updates, u => u.State == StageState.Running && u.Activity is { Kind: ActivityKind.Fetch });
        Assert.Equal(StageState.NeedsReview, updates[^1].State);
        Assert.Equal("Research: needs review", (await client.GetRecentProjectsAsync()).Single().StatusLine);
    }

    [Fact]
    public async Task Without_the_research_gate_the_sheet_is_approved_and_the_run_goes_on()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client, s => s with { Gates = [.. s.Gates.Where(g => g != PipelineStage.Research)] });

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Approved, await WaitForAsync(client, project.Id, StageState.Approved, StageState.NeedsReview, StageState.Failed));
        Assert.Equal(1, (await client.GetFactSheetAsync(project.Id)).ApprovedVersion);
    }

    [Fact]
    public async Task Run_through_mode_does_not_stop_at_the_research_gate()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client, s => s with { Mode = RunMode.RunThrough });

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Approved, await WaitForAsync(client, project.Id, StageState.Approved, StageState.NeedsReview, StageState.Failed));
    }

    [Fact]
    public async Task An_invalid_answer_goes_back_with_its_problems_and_a_fixed_one_is_kept()
    {
        _agent.Answer(Invented()).Answer(Invented()).Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.NeedsReview, await WaitForAsync(client, project.Id, StageState.NeedsReview, StageState.Failed));
        Assert.Equal(3, _agent.Calls.Count);
        Assert.Empty(_agent.Calls[0].Problems);
        Assert.Contains("F01: the quote is not on", _agent.Calls[1].Problems.Single());
        Assert.Equal("session-1", _agent.Calls[1].Session);   // the same conversation fixes its answer
        var activity = (await client.GetFactSheetAsync(project.Id)).Activity;
        Assert.Contains(activity, a => a.Kind == ActivityKind.Check && a.Text.StartsWith("try 1: F01: the quote is not on", StringComparison.Ordinal) && a.Text.EndsWith("sent back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_three_invalid_answers_the_stage_fails_with_the_reason_and_Retry_runs_it_again()
    {
        _agent.Answer(Invented()).Answer(Invented()).Answer(Invented());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Failed, await WaitForAsync(client, project.Id, StageState.Failed, StageState.NeedsReview));
        var failed = await client.GetFactSheetAsync(project.Id);
        Assert.StartsWith("The answer was not a valid fact sheet after 3 tries. Last problem: F01: the quote is not on", failed.Error);
        Assert.Null(failed.Sheet);
        Assert.Contains(failed.Activity, a => a.Text.StartsWith("try 3:", StringComparison.Ordinal) && a.Text.EndsWith("stage failed", StringComparison.Ordinal));

        _agent.Answer(Good());
        await client.RegenerateAsync(project.Id, PipelineStage.Research);

        Assert.Equal(StageState.NeedsReview, await WaitForAsync(client, project.Id, StageState.NeedsReview));
        Assert.Null((await client.GetFactSheetAsync(project.Id)).Error);
    }

    [Fact]
    public async Task A_model_that_cannot_be_asked_fails_the_stage_at_once()
    {
        _agent.Fail("Claude CLI was not found (claude). Check its executable in Settings.");
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Failed, await WaitForAsync(client, project.Id, StageState.Failed, StageState.NeedsReview));
        Assert.Equal("Claude CLI was not found (claude). Check its executable in Settings.", (await client.GetFactSheetAsync(project.Id)).Error);
        Assert.Single(_agent.Calls);
    }

    [Fact]
    public async Task A_project_on_LM_Studio_fails_research_with_the_reason()
    {
        var client = await StartAsync();
        var project = await NewProjectAsync(client, s => s with { Writing = s.Writing with { Provider = "LM Studio" } });

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Failed, await WaitForAsync(client, project.Id, StageState.Failed));
        Assert.Contains("#19", (await client.GetFactSheetAsync(project.Id)).Error);
        Assert.Empty(_agent.Calls);
    }

    [Fact]
    public async Task Regenerate_makes_a_new_version_and_keeps_the_old_one()
    {
        _agent.Answer(Good("First.")).Answer(Good("Second."));
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateAsync(project.Id, PipelineStage.Research);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        var view = await client.GetFactSheetAsync(project.Id);
        Assert.Equal([1, 2], view.Versions.Select(v => v.Version));
        Assert.Equal("Second.", view.Sheet!.Facts[0].Statement);
        Assert.Equal("First.", (await client.GetFactSheetAsync(project.Id, 1)).Sheet!.Facts[0].Statement);
    }

    [Fact]
    public async Task Approving_a_version_marks_research_approved()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        await client.ApproveAsync(project.Id, PipelineStage.Research, 1);

        Assert.Equal(StageState.Approved, (await client.GetProjectAsync(project.Id)).Stages[0].State);
        Assert.Equal(1, (await client.GetFactSheetAsync(project.Id)).ApprovedVersion);
        // The script stage does not exist yet, so the project says where it really stands.
        Assert.Equal("Research: approved", (await client.GetRecentProjectsAsync()).Single().StatusLine);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.ApproveAsync(project.Id, PipelineStage.Research, 7));
    }

    [Fact]
    public async Task Cancel_puts_the_stage_back_where_it_was()
    {
        _agent.Hold();
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.Running);
        await _agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Opened while it runs, the screen gets what the run has done so far.
        Assert.Contains((await client.GetFactSheetAsync(project.Id)).Activity, a => a.Kind == ActivityKind.Fetch);

        await client.CancelAsync(project.Id, PipelineStage.Research);

        Assert.Equal(StageState.NotStarted, await WaitForAsync(client, project.Id, StageState.NotStarted, StageState.Failed));
        Assert.Contains((await client.GetFactSheetAsync(project.Id)).Activity, a => a.Text == "cancelled");
    }

    [Fact]
    public async Task Cancelling_a_waiting_run_puts_it_back_at_once_not_after_the_run_before_it()
    {
        _agent.Hold();
        var client = await StartAsync();
        var first = await NewProjectAsync(client);
        var second = await NewProjectAsync(client);
        await client.StartRunAsync(first.Id);
        await _agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.StartRunAsync(second.Id);
        Assert.Equal(StageState.Running, (await client.GetProjectAsync(second.Id)).Stages[0].State);   // waiting its turn

        await client.CancelAsync(second.Id, PipelineStage.Research);

        Assert.Equal(StageState.NotStarted, (await client.GetProjectAsync(second.Id)).Stages[0].State);
        Assert.Equal(StageState.Running, (await client.GetProjectAsync(first.Id)).Stages[0].State);
        _agent.Answer(Good());
        await client.RegenerateAsync(second.Id, PipelineStage.Research);   // free to start again
        await client.CancelAsync(first.Id, PipelineStage.Research);
        Assert.Equal(StageState.NeedsReview, await WaitForAsync(client, second.Id, StageState.NeedsReview, StageState.Failed));
    }

    [Fact]
    public async Task Facts_cannot_change_while_research_runs()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);
        _agent.Hold();
        await client.RegenerateAsync(project.Id, PipelineStage.Research);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Weight: 9)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveAsync(project.Id, PipelineStage.Research, 1));
        await client.CancelAsync(project.Id, PipelineStage.Research);
        Assert.Equal(StageState.NeedsReview, await WaitForAsync(client, project.Id, StageState.NeedsReview));
    }

    [Fact]
    public async Task A_stage_left_running_when_the_app_closed_is_failed_on_the_next_start()
    {
        var first = await StartAsync();
        var project = await NewProjectAsync(first);
        await using (var db = await _engine.DbAsync())
        {
            db.Cells.Add(new CellEntry { ProjectId = project.Id, Stage = PipelineStage.Research, State = StageState.Running });
            await db.SaveChangesAsync();
        }

        var second = await StartAsync();

        var view = await second.GetFactSheetAsync(project.Id);
        Assert.Equal(StageState.Failed, view.State);
        Assert.Equal(PipelineRunner.ClosedWhileRunning, view.Error);
    }

    [Fact]
    public async Task Your_changes_collect_in_one_edited_version_until_you_approve_it()
    {
        _agent.Answer(Good("Coins hold souls.", "Coins are money."));
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        // The generated v1 is never changed: the first change makes v2.
        var weighted = await client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Weight: 10));
        Assert.Equal(2, weighted.Version);
        Assert.Equal(new ResultVersion(2, VersionOrigin.Edited, weighted.Versions[1].CreatedAt, BasedOn: 1), weighted.Versions[1]);
        Assert.Equal(Fact.DefaultWeight, (await client.GetFactSheetAsync(project.Id, 1)).Sheet!.Facts[0].Weight);

        // More changes go into v2 while it is not approved.
        var reworded = await client.ChangeFactAsync(project.Id, 2, "F02", new FactChange(Statement: " Coins are money in Hell. ", LeftOut: true));
        Assert.Equal(2, reworded.Version);
        Assert.Equal(2, reworded.Versions.Count);
        Assert.Equal(
            [new Fact("F01", "Coins hold souls.", Page, "a single mortal soul is bound", 10, false), new Fact("F02", "Coins are money in Hell.", Page, "a single mortal soul is bound", 5, true)],
            reworded.Sheet!.Facts);

        // Once approved, v2 is fixed too; a change after that is v3 and needs approving again.
        await client.ApproveAsync(project.Id, PipelineStage.Research, 2);
        var again = await client.ChangeFactAsync(project.Id, 2, "F02", new FactChange(LeftOut: false));
        Assert.Equal(3, again.Version);
        Assert.Equal(StageState.NeedsReview, again.State);
        Assert.Equal(2, again.ApprovedVersion);
        Assert.True((await client.GetFactSheetAsync(project.Id, 2)).Sheet!.Facts[1].LeftOut);
    }

    [Fact]
    public async Task Changing_a_fact_moves_its_project_to_the_top_of_Recent_projects()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var older = await NewProjectAsync(client);
        await client.StartRunAsync(older.Id);
        await WaitForAsync(client, older.Id, StageState.NeedsReview);
        var newer = await NewProjectAsync(client);
        Assert.Equal(newer.Id, (await client.GetRecentProjectsAsync())[0].Id);

        await client.ChangeFactAsync(older.Id, 1, "F01", new FactChange(Weight: 8));

        Assert.Equal(older.Id, (await client.GetRecentProjectsAsync())[0].Id);
    }

    [Fact]
    public async Task A_failure_names_how_many_tries_there_really_were()
    {
        _agent.WithoutSession = true;   // nothing to send the problems back to
        _agent.Answer(Invented());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);

        Assert.Equal(StageState.Failed, await WaitForAsync(client, project.Id, StageState.Failed, StageState.NeedsReview));
        Assert.StartsWith("The answer was not a valid fact sheet after 1 try.", (await client.GetFactSheetAsync(project.Id)).Error);
    }

    [Fact]
    public async Task A_change_that_changes_nothing_makes_no_version()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        var view = await client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Weight: Fact.DefaultWeight, LeftOut: false));

        Assert.Equal(1, view.Version);
        Assert.Single(view.Versions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Weights_go_from_1_to_10(int weight)
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);
        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Weight: weight)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Statement: "  ")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.ChangeFactAsync(project.Id, 1, "F99", new FactChange(Weight: 3)));
        Assert.Single((await client.GetFactSheetAsync(project.Id)).Versions);
    }

    [Fact]
    public async Task A_project_that_never_ran_has_an_empty_fact_sheet()
    {
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        var view = await client.GetFactSheetAsync(project.Id);

        Assert.Equal(StageState.NotStarted, view.State);
        Assert.Empty(view.Versions);
        Assert.Null(view.Version);
        Assert.Null(view.Sheet);
        Assert.Empty(view.Activity);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetFactSheetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_request_carries_the_brief_sources_and_profile_instructions()
    {
        _agent.Answer(Good());
        var client = await StartAsync();
        var project = await NewProjectAsync(client);

        await client.StartRunAsync(project.Id);
        await WaitForAsync(client, project.Id, StageState.NeedsReview);

        var request = _agent.Calls.Single().Request;
        Assert.Equal(project.Setup.Brief, request.Brief);
        Assert.Equal(["bg3.wiki", "forgottenrealms.fandom.com"], request.Sources);
        Assert.StartsWith("Collect the facts the script needs", request.Instructions);
        Assert.Equal(("English", 240), (request.Language, request.TargetSeconds));
    }

    /// <summary>Answers from a script: a fact sheet, a failure, or a run that waits until cancelled.</summary>
    private sealed class FakeResearchAgent : IResearchAgent
    {
        private readonly Queue<Func<CancellationToken, Task<ResearchOutput>>> _answers = new();

        public List<(ResearchRequest Request, IReadOnlyList<string> Problems, string? Session)> Calls { get; } = [];

        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Answers carry no session, as when Claude CLI does not say one.</summary>
        public bool WithoutSession { get; set; }

        public FakeResearchAgent Answer(ResearchOutput output)
        {
            _answers.Enqueue(_ => Task.FromResult(output));
            return this;
        }

        public void Fail(string reason) => _answers.Enqueue(_ => throw new StageFailedException(reason));

        public void Hold()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _answers.Enqueue(async token =>
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            });
        }

        public async Task<ResearchAnswer> AskAsync(
            ResearchRequest request, ResearchAnswer? previous, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((request, problems, previous?.Session));
            }
            activity.Report(new ActivityLine(DateTimeOffset.UtcNow, ActivityKind.Fetch, "bg3.wiki/wiki/Soul_Coin"));
            var output = await _answers.Dequeue()(cancellationToken);
            var pages = new ResearchPages();
            pages.Add(Page, null, PageText);
            return new ResearchAnswer(WithoutSession ? null : "session-1", output, pages);
        }
    }
}
