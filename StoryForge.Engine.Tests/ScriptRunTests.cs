using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Script stage in the pipeline, with scripted models: it starts when the fact sheet is approved,
/// writes segments as cells of their own, and each segment is approved, reworded, switched back or
/// written again on its own.
/// </summary>
public sealed class ScriptRunTests : IDisposable
{
    private const string Page = "https://bg3.wiki/wiki/Soul_Coin";
    private const string PageText = "Soul Coins are small, coin-shaped objects forged of infernal iron into which a single mortal soul is bound.";

    private readonly EngineTestHost _engine = new();
    private readonly FakeScriptAgent _script = new();
    private readonly ThreeFacts _research = new();

    public void Dispose() => _engine.Dispose();

    private Task<IStoryForgeClient> StartAsync() => _engine.StartClientAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(_research));
        services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(_script));
    });

    /// <summary>A project whose fact sheet is researched and approved (F01 marked must), so the script starts.</summary>
    private static async Task<Project> ApprovedFactsAsync(IStoryForgeClient client, Func<ProjectSetup, ProjectSetup>? change = null)
    {
        var setup = await ProjectTests.ValidSetup(client);
        var project = await client.CreateProjectAsync(change is null ? setup : change(setup));
        await client.StartRunAsync(project.Id);
        await WaitForAsync(() => client.GetFactSheetAsync(project.Id), v => v.State is StageState.NeedsReview or StageState.Approved);
        var sheet = await client.GetFactSheetAsync(project.Id);
        if (sheet.State == StageState.NeedsReview)
        {
            var edited = await client.ChangeFactAsync(project.Id, 1, "F01", new FactChange(Weight: 10));
            await client.ApproveAsync(project.Id, PipelineStage.Research, edited.Version!.Value);
        }
        return project;
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Still {value}.");
            }
            await Task.Delay(20);
        }
    }

    private static Task<ScriptView> ScriptAsync(IStoryForgeClient client, Guid projectId, params StageState[] states) =>
        WaitForAsync(() => client.GetScriptAsync(projectId), v => states.Contains(v.State));

    private static ScriptPart Part(string title, params string[] facts) =>
        new(title, string.Join(' ', Enumerable.Repeat("word", 150)), facts);

    /// <summary>Three segments of a minute each: right for a 4:00 target with F01 and F02 in use.</summary>
    private static ScriptOutput Good() => new([Part("Hook - a coin that screams", "F01"), Part("Money of Hell", "F02"), Part("Outro", "F01"), Part("Coins in the game", "F03")], "");

    [Fact]
    public async Task Approving_the_fact_sheet_writes_the_script_as_one_row_per_segment()
    {
        _script.Answer(Good());
        var client = await StartAsync();

        var project = await ApprovedFactsAsync(client);
        var script = await ScriptAsync(client, project.Id, StageState.NeedsReview, StageState.Failed);

        Assert.Equal(StageState.NeedsReview, script.State);
        Assert.Equal(["S01", "S02", "S03", "S04"], script.Segments.Select(s => s.Id));
        Assert.Equal("Hook - a coin that screams", script.Segments[0].Title);
        Assert.All(script.Segments, s => Assert.Equal((StageState.NeedsReview, 1, 60), (s.State, s.Version, s.Seconds)));
        Assert.Equal(240, script.Seconds);
        Assert.Equal(["F01", "F02", "F03"], script.Facts.Select(f => f.Id));
        Assert.Equal("Script: needs review", (await client.GetRecentProjectsAsync()).Single().StatusLine);
    }

    [Fact]
    public async Task The_script_is_written_from_the_approved_sheet_brief_and_script_profile()
    {
        _script.Answer(Good());
        var client = await StartAsync();

        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        var request = _script.Requests.Single();
        Assert.Equal(project.Setup.Brief, request.Brief);
        Assert.Equal(Fact.MustWeight, request.Facts.Facts.Single(f => f.Id == "F01").Weight);   // v2, the approved one
        Assert.StartsWith("Write the narration for the brief", request.Instructions);
        Assert.Equal(("English", 240), (request.Language, request.TargetSeconds));
    }

    [Fact]
    public async Task A_script_that_drops_a_must_fact_goes_back_and_a_fixed_one_is_kept()
    {
        _script.Answer(new([Part("Money", "F02"), Part("More money", "F02"), Part("Even more", "F03"), Part("Outro", "F03")], "")).Answer(Good());
        var client = await StartAsync();

        var project = await ApprovedFactsAsync(client);
        var script = await ScriptAsync(client, project.Id, StageState.NeedsReview, StageState.Failed);

        Assert.Equal(StageState.NeedsReview, script.State);
        Assert.Equal(["F01 is marked must, but no segment uses it."], _script.Problems[1]);
        Assert.Contains(script.Activity, a => a.Text.StartsWith("try 1: F01 is marked must", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_three_bad_scripts_the_stage_fails_with_the_reason()
    {
        var bad = new ScriptOutput([Part("War", "F99")], "");
        _script.Answer(bad).Answer(bad).Answer(bad);
        var client = await StartAsync();

        var project = await ApprovedFactsAsync(client);
        var script = await ScriptAsync(client, project.Id, StageState.Failed, StageState.NeedsReview);

        Assert.StartsWith("The answer was not a valid script after 3 tries. Last problem: S01 uses F99", script.Error);
        Assert.Empty(script.Segments);
    }

    [Fact]
    public async Task Without_the_script_gate_every_segment_is_approved_at_once()
    {
        _script.Answer(Good());
        var client = await StartAsync();

        var project = await ApprovedFactsAsync(client, s => s with { Gates = [.. s.Gates.Where(g => g != PipelineStage.Script)] });
        var script = await ScriptAsync(client, project.Id, StageState.Approved, StageState.NeedsReview, StageState.Failed);

        Assert.Equal(StageState.Approved, script.State);
        Assert.All(script.Segments, s => Assert.Equal((StageState.Approved, (int?)1), (s.State, s.ApprovedVersion)));
    }

    [Fact]
    public async Task The_script_is_approved_once_every_segment_is()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.ApproveSegmentAsync(project.Id, "S02", 1);
        var one = await client.GetScriptAsync(project.Id);
        Assert.Equal(StageState.NeedsReview, one.State);
        Assert.Equal(StageState.Approved, one.Segments[1].State);
        Assert.Equal(StageState.NeedsReview, one.Segments[0].State);

        await client.ApproveScriptAsync(project.Id);   // "Approve remaining"
        var all = await client.GetScriptAsync(project.Id);
        Assert.Equal(StageState.Approved, all.State);
        Assert.All(all.Segments, s => Assert.Equal(StageState.Approved, s.State));
        Assert.Equal(StageState.Approved, (await client.GetProjectAsync(project.Id)).Stages[1].State);
    }

    [Fact]
    public async Task Rewording_a_segment_is_a_new_version_and_an_older_one_can_come_back()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.ApproveScriptAsync(project.Id);

        await client.EditSegmentAsync(project.Id, "S01", "Hook - a scream in the dark", " A coin screams. ");
        var edited = (await client.GetScriptAsync(project.Id)).Segments[0];
        Assert.Equal((2, StageState.NeedsReview, "A coin screams."), (edited.Version, edited.State, edited.Narration));
        Assert.Equal(new ResultVersion(2, VersionOrigin.Edited, edited.Versions[1].CreatedAt, 1), edited.Versions[1]);
        Assert.Equal(["F01"], edited.FactIds);
        Assert.Equal(StageState.NeedsReview, (await client.GetScriptAsync(project.Id)).State);

        await client.SelectSegmentVersionAsync(project.Id, "S01", 1);   // back to the approved one
        var back = await client.GetScriptAsync(project.Id);
        Assert.Equal((1, StageState.Approved), (back.Segments[0].Version, back.Segments[0].State));
        Assert.Equal(StageState.Approved, back.State);
        Assert.Equal(2, back.Segments[0].Versions.Count);
    }

    [Fact]
    public async Task Writing_one_segment_again_leaves_the_others_as_they_are()
    {
        _script.Answer(Good());
        _script.AnswerSegment(Part("Hook - a coin that whispers", "F01"));
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.ApproveScriptAsync(project.Id);

        await client.RegenerateSegmentAsync(project.Id, "S01");
        var script = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[0].Version == 2 || v.Segments[0].State == StageState.Failed);

        Assert.Equal(("Hook - a coin that whispers", StageState.NeedsReview), (script.Segments[0].Title, script.Segments[0].State));
        Assert.All(script.Segments.Skip(1), s => Assert.Equal((1, StageState.Approved), (s.Version, s.State)));
        Assert.Equal(StageState.NeedsReview, script.State);
        Assert.Equal("S01", _script.Rewrites.Single());
    }

    [Fact]
    public async Task A_rewritten_segment_that_drops_its_must_fact_goes_back()
    {
        _script.Answer(new([Part("Hook", "F01"), Part("Money", "F02"), Part("More", "F03"), Part("Outro", "F02")], ""));
        _script.AnswerSegment(Part("Hook", "F02")).AnswerSegment(Part("Hook - kept", "F01"));
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateSegmentAsync(project.Id, "S01");
        var script = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[0].Version == 2 || v.Segments[0].State == StageState.Failed);

        Assert.Equal("Hook - kept", script.Segments[0].Title);
        Assert.Equal(["F01 is marked must and no other segment uses it, so S01 has to keep it."], _script.Problems[^1]);
    }

    [Fact]
    public async Task A_segment_that_cannot_be_written_again_fails_alone()
    {
        _script.Answer(Good());
        _script.FailSegment("Claude CLI was not found (claude). Check its executable in Settings.");
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateSegmentAsync(project.Id, "S02");
        var script = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[1].State == StageState.Failed);

        Assert.Equal("Claude CLI was not found (claude). Check its executable in Settings.", script.Segments[1].Error);
        Assert.Equal(1, script.Segments[1].Version);   // its text is still there
        Assert.Equal(StageState.NeedsReview, script.State);
    }

    [Fact]
    public async Task A_segment_that_fails_to_be_written_again_takes_the_approved_script_back_to_review()
    {
        _script.Answer(Good());
        _script.FailSegment("Claude CLI did not answer in time.");
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.ApproveScriptAsync(project.Id);

        await client.RegenerateSegmentAsync(project.Id, "S02");
        var script = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[1].State == StageState.Failed);

        Assert.Equal(StageState.NeedsReview, script.State);
        Assert.Equal(StageState.NeedsReview, (await client.GetProjectAsync(project.Id)).Stages[1].State);
    }

    [Fact]
    public async Task After_a_failed_rewrite_of_the_whole_script_the_script_before_it_can_still_be_approved()
    {
        var bad = new ScriptOutput([Part("War", "F99")], "");
        _script.Answer(Good()).Answer(bad).Answer(bad).Answer(bad);
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.RegenerateAsync(project.Id, PipelineStage.Script);
        await ScriptAsync(client, project.Id, StageState.Failed);

        await client.ApproveScriptAsync(project.Id);

        var script = await client.GetScriptAsync(project.Id);
        Assert.Equal((StageState.Approved, null), (script.State, script.Error));
        Assert.Equal(StageState.Approved, (await client.GetProjectAsync(project.Id)).Stages[1].State);
    }

    [Fact]
    public async Task A_segment_left_running_when_the_app_closed_takes_the_approved_script_back_to_review()
    {
        _script.Answer(Good());
        var first = await StartAsync();
        var project = await ApprovedFactsAsync(first);
        await ScriptAsync(first, project.Id, StageState.NeedsReview);
        await first.ApproveScriptAsync(project.Id);
        await using (var db = await _engine.DbAsync())
        {
            db.Cells.Single(c => c.ProjectId == project.Id && c.Key == "S03").State = StageState.Running;
            await db.SaveChangesAsync();
        }

        var second = await StartAsync();

        var script = await second.GetScriptAsync(project.Id);
        Assert.Equal((StageState.Failed, PipelineRunner.ClosedWhileRunning), (script.Segments[2].State, script.Segments[2].Error));
        Assert.Equal(StageState.NeedsReview, script.State);
    }

    [Fact]
    public async Task Without_the_script_gate_your_own_wording_and_version_switches_are_approved()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client, s => s with { Gates = [.. s.Gates.Where(g => g != PipelineStage.Script)] });
        await ScriptAsync(client, project.Id, StageState.Approved, StageState.NeedsReview, StageState.Failed);

        await client.EditSegmentAsync(project.Id, "S01", "Hook - mine", "My words.");
        var edited = await client.GetScriptAsync(project.Id);
        Assert.Equal((StageState.Approved, (int?)2), (edited.Segments[0].State, edited.Segments[0].ApprovedVersion));
        Assert.Equal(StageState.Approved, edited.State);

        await client.SelectSegmentVersionAsync(project.Id, "S01", 1);
        var back = await client.GetScriptAsync(project.Id);
        Assert.Equal((StageState.Approved, (int?)1), (back.Segments[0].State, back.Segments[0].ApprovedVersion));
        Assert.Equal(StageState.Approved, back.State);
    }

    [Fact]
    public async Task A_change_to_the_facts_the_script_was_written_from_makes_a_new_version()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);   // the script is written from v2, F01 marked must
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.ApproveAsync(project.Id, PipelineStage.Research, 1);   // v2 is the latest and not approved now

        var changed = await client.ChangeFactAsync(project.Id, 2, "F02", new FactChange(Statement: "Soul coins rust."));

        Assert.Equal(3, changed.Version);
        var script = await client.GetScriptAsync(project.Id);
        Assert.Equal(("Soul coins are forged of infernal iron.", Fact.MustWeight), (script.Facts[1].Statement, script.Facts[0].Weight));
    }

    [Fact]
    public async Task Writing_the_whole_script_again_replaces_its_segments()
    {
        _script.Answer(Good()).Answer(new([Part("Hook", "F01"), Part("Money", "F02", "F03")], ""));
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateAsync(project.Id, PipelineStage.Script);
        var script = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments.Count == 2 || v.State == StageState.Failed);

        Assert.Equal(["S01", "S02"], script.Segments.Select(s => s.Id));
        Assert.Equal(2, script.Segments[0].Version);
    }

    [Fact]
    public async Task Changes_to_a_segment_are_refused_while_it_is_written()
    {
        _script.Answer(Good());
        _script.HoldSegment();
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateSegmentAsync(project.Id, "S01");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EditSegmentAsync(project.Id, "S01", "t", "n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveScriptAsync(project.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegenerateAsync(project.Id, PipelineStage.Script));
        await client.CancelAsync(project.Id, PipelineStage.Script);   // the whole stage is not running: nothing to cancel
    }

    [Fact]
    public async Task Approving_the_fact_sheet_again_keeps_the_script_already_written()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.ApproveSegmentAsync(project.Id, "S02", 1);
        await client.EditSegmentAsync(project.Id, "S01", "Hook - mine", "My words.");

        await ApproveNewResearchAsync(client, project.Id);

        var script = await client.GetScriptAsync(project.Id);
        Assert.Equal(StageState.NeedsReview, script.State);
        Assert.Equal(("Hook - mine", 2), (script.Segments[0].Title, script.Segments[0].Version));
        Assert.Equal(StageState.Approved, script.Segments[1].State);
        Assert.Single(_script.Requests);
    }

    [Fact]
    public async Task The_script_keeps_to_the_fact_sheet_it_was_written_from()
    {
        _script.Answer(Good());
        _script.AnswerSegment(Part("Hook - a coin that whispers", "F01"));
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        _research.First = "Soul coins scream when spent.";
        await ApproveNewResearchAsync(client, project.Id);

        var script = await client.GetScriptAsync(project.Id);
        Assert.Equal(("Soul coins hold one soul.", Fact.MustWeight), (script.Facts[0].Statement, script.Facts[0].Weight));

        await client.RegenerateSegmentAsync(project.Id, "S01");
        await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[0].Version == 2 || v.Segments[0].State == StageState.Failed);
        Assert.Equal("Soul coins hold one soul.", _script.RewriteRequests.Single().Facts.Facts[0].Statement);
    }

    [Fact]
    public async Task The_length_note_shows_only_while_the_script_runs_over_the_target()
    {
        var longer = new ScriptPart("Part", string.Join(' ', Enumerable.Repeat("word", 190)), ["F01"]);
        _script.Answer(new([longer, longer with { FactIds = ["F02"] }, longer, longer with { FactIds = ["F03"] }], "The must facts need about 5:00."));
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        var script = await ScriptAsync(client, project.Id, StageState.NeedsReview, StageState.Failed);
        Assert.Equal("The must facts need about 5:00.", script.LengthNote);

        await client.EditSegmentAsync(project.Id, "S01", "Hook", "A coin screams.");

        Assert.Equal("", (await client.GetScriptAsync(project.Id)).LengthNote);
    }

    [Fact]
    public async Task A_segment_being_written_shows_its_log_and_can_be_cancelled()
    {
        _script.Answer(Good());
        _script.HoldSegment();
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await client.RegenerateSegmentAsync(project.Id, "S02");
        var running = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[1].Activity.Count > 0);
        Assert.Equal(StageState.Running, running.Segments[1].State);
        Assert.Equal(["rewriting S02"], running.Segments[1].Activity.Select(a => a.Text));
        Assert.DoesNotContain(running.Activity, a => a.Text == "rewriting S02");   // the whole script's log is not this segment's

        await client.CancelSegmentAsync(project.Id, "S02");

        var back = await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[1].State != StageState.Running);
        Assert.Equal((StageState.NeedsReview, 1), (back.Segments[1].State, back.Segments[1].Version));
    }

    [Fact]
    public async Task A_segment_that_is_gone_when_its_rewrite_ends_stays_gone()
    {
        _script.Answer(Good());
        _script.HoldSegment();
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);
        await client.RegenerateSegmentAsync(project.Id, "S04");
        await WaitForAsync(() => client.GetScriptAsync(project.Id), v => v.Segments[3].Activity.Count > 0);

        // A whole new script without S04 landed meanwhile.
        await using (var db = await _engine.DbAsync())
        {
            db.Cells.Remove(db.Cells.Single(c => c.ProjectId == project.Id && c.Key == "S04"));
            await db.SaveChangesAsync();
        }
        await client.CancelSegmentAsync(project.Id, "S04");

        Assert.Equal(["S01", "S02", "S03"], (await client.GetScriptAsync(project.Id)).Segments.Select(s => s.Id));
        await using (var db = await _engine.DbAsync())
        {
            Assert.DoesNotContain(db.Cells, c => c.ProjectId == project.Id && c.Key == "S04");   // no empty cell to hold up the approval
        }
    }

    /// <summary>Researches the fact sheet again and approves the new version as it comes.</summary>
    private static async Task ApproveNewResearchAsync(IStoryForgeClient client, Guid projectId)
    {
        var before = (await client.GetFactSheetAsync(projectId)).Version;
        await client.RegenerateAsync(projectId, PipelineStage.Research);
        var sheet = await WaitForAsync(() => client.GetFactSheetAsync(projectId), v => v.Version > before && v.State != StageState.Running);
        await client.ApproveAsync(projectId, PipelineStage.Research, sheet.Version!.Value);
    }

    [Fact]
    public async Task Without_an_approved_fact_sheet_the_script_says_so()
    {
        var client = await StartAsync();
        var project = await client.CreateProjectAsync(await ProjectTests.ValidSetup(client));

        await client.RegenerateAsync(project.Id, PipelineStage.Script);
        var script = await ScriptAsync(client, project.Id, StageState.Failed);

        Assert.Equal("The fact sheet is not approved yet. Approve it, and the script is written from it.", script.Error);
        Assert.Empty(_script.Requests);
    }

    [Fact]
    public async Task Bad_input_is_refused_with_a_reason()
    {
        _script.Answer(Good());
        var client = await StartAsync();
        var project = await ApprovedFactsAsync(client);
        await ScriptAsync(client, project.Id, StageState.NeedsReview);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EditSegmentAsync(project.Id, "S01", " ", "text"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.EditSegmentAsync(project.Id, "S09", "t", "n"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.ApproveSegmentAsync(project.Id, "S01", 7));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.RegenerateSegmentAsync(project.Id, "S09"));
    }

    /// <summary>Research that always finds the same three facts on one page.</summary>
    private sealed class ThreeFacts : IResearchAgent
    {
        /// <summary>What the first fact says; a test changes it to research something new.</summary>
        public string First { get; set; } = "Soul coins hold one soul.";

        public Task<ResearchAnswer> AskAsync(ResearchRequest request, ResearchAnswer? previous, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            var pages = new ResearchPages();
            pages.Add(Page, null, PageText);
            return Task.FromResult(new ResearchAnswer("r", new ResearchOutput(
            [
                new ResearchFact(First, Page, "a single mortal soul is bound"),
                new ResearchFact("Soul coins are forged of infernal iron.", Page, "forged of infernal iron"),
                new ResearchFact("Soul coins are small.", Page, "Soul Coins are small"),
            ]), pages));
        }
    }

    /// <summary>Scripts and segments from a script; records what it was asked.</summary>
    private sealed class FakeScriptAgent : IScriptAgent
    {
        private readonly Queue<ScriptOutput> _scripts = new();
        private readonly Queue<Func<CancellationToken, Task<ScriptPart>>> _segments = new();

        public List<ScriptRequest> Requests { get; } = [];

        public List<IReadOnlyList<string>> Problems { get; } = [];

        public List<string> Rewrites { get; } = [];

        public List<ScriptRequest> RewriteRequests { get; } = [];

        public FakeScriptAgent Answer(ScriptOutput output)
        {
            _scripts.Enqueue(output);
            return this;
        }

        public FakeScriptAgent AnswerSegment(ScriptPart part)
        {
            _segments.Enqueue(_ => Task.FromResult(part));
            return this;
        }

        public void FailSegment(string reason) => _segments.Enqueue(_ => throw new StageFailedException(reason));

        public void HoldSegment() => _segments.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        public Task<ScriptAnswer<ScriptOutput>> WriteAsync(ScriptRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                Problems.Add(problems);
            }
            return Task.FromResult(new ScriptAnswer<ScriptOutput>("s", _scripts.Dequeue()));
        }

        public async Task<ScriptAnswer<ScriptPart>> RewriteAsync(ScriptRequest request, IReadOnlyList<Segment> script, string segmentId, string? session,
            IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Rewrites.Add(segmentId);
                RewriteRequests.Add(request);
                Problems.Add(problems);
            }
            activity.Report(new ActivityLine(DateTimeOffset.UtcNow, ActivityKind.Model, $"rewriting {segmentId}"));
            return new ScriptAnswer<ScriptPart>("s", await _segments.Dequeue()(cancellationToken));
        }
    }
}
