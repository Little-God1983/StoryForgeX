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

    public void Dispose() => _engine.Dispose();

    private Task<IStoryForgeClient> StartAsync() => _engine.StartClientAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new ThreeFacts()));
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
        public Task<ResearchAnswer> AskAsync(ResearchRequest request, ResearchAnswer? previous, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            var pages = new ResearchPages();
            pages.Add(Page, null, PageText);
            return Task.FromResult(new ResearchAnswer("r", new ResearchOutput(
            [
                new ResearchFact("Soul coins hold one soul.", Page, "a single mortal soul is bound"),
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
                Problems.Add(problems);
            }
            return new ScriptAnswer<ScriptPart>("s", await _segments.Dequeue()(cancellationToken));
        }
    }
}
