using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Research;
using StoryForge.Engine.Script;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Storyboard stage in the pipeline, with a scripted model and a stand-in voice: it starts when
/// the voice is approved, always stops at its gate, and each shot is written again or approved on its own.
/// </summary>
public sealed class StoryboardRunTests : IDisposable
{
    private readonly EngineTestHost _engine = new();
    private readonly ScriptRunTests.FakeScriptAgent _script = new();
    private readonly FakeStoryboardAgent _board = new();

    public void Dispose() => _engine.Dispose();

    private readonly FakeFactCheck _facts = new();

    private Task<IStoryForgeClient> StartAsync() => _engine.StartClientAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IResearchAgent>(new ScriptRunTests.ThreeFacts()));
        services.Replace(ServiceDescriptor.Singleton<IScriptAgent>(_script));
        services.Replace(ServiceDescriptor.Singleton<IStoryboardAgent>(_board));
        services.Replace(ServiceDescriptor.Singleton<IStoryboardFactCheck>(_facts));
        UseVoice<WordsVoice>(services);
    });

    /// <summary>Puts a stand-in in the Voice stage's place: no ComfyUI, no ffmpeg.</summary>
    internal static void UseVoice<T>(IServiceCollection services) where T : class, IStageWorker
    {
        services.RemoveAll<IStageWorker>();
        services.AddSingleton<IStageWorker, ResearchStage>();
        services.AddSingleton<IStageWorker, ScriptStage>();
        services.AddSingleton<IStageWorker, T>();
        services.AddSingleton<IStageWorker, StoryboardStage>();
        services.AddSingleton<IStageWorker, References.ReferencesStage>();
    }

    /// <summary>A project whose voice is approved (no Script or Voice gate), so the storyboard starts by itself.</summary>
    internal async Task<Project> VoiceApprovedAsync(IStoryForgeClient client, Func<ProjectSetup, ProjectSetup>? change = null)
    {
        _script.Answer(ScriptRunTests.Good());
        return await ScriptRunTests.ApprovedFactsAsync(client, s =>
        {
            s = s with { Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Script or PipelineStage.Voice))] };
            return change is null ? s : change(s);
        });
    }

    /// <summary>A good storyboard for the script of <see cref="ScriptRunTests.Good"/>: 150 words a segment, a shot every 10 words (5 s).</summary>
    internal static StoryboardOutput Good() => new StoryboardOutput(
        [new("Karlach", "character", "", "The look of Karlach."), new("Soul coin", "object", "", "The look of Soul coin."), new("Mammon's mint", "place", "Where every coin is made.", "The look of Mammon's mint.")],
        [.. new[] { "S01", "S02", "S03", "S04" }.Select(id => new SegmentShotsOutput(id,
            [.. Enumerable.Range(0, 15).Select(i => new ShotOutput(i * 10, $"{id} shot {i + 1}", i == 0 ? ["Soul coin"] : ["Karlach"], i % 2 == 1, []))]))]).WithPicks();

    private async Task<List<(string Key, StageState State, int? Version, Shot Shot)>> ShotsAsync()
    {
        await using var db = await _engine.DbAsync();
        var cells = await db.Cells.AsNoTracking().Where(c => c.Stage == PipelineStage.Storyboard && c.Key != "").ToListAsync();
        var versions = await db.CellVersions.AsNoTracking().Where(v => v.Stage == PipelineStage.Storyboard && v.Key != "").ToListAsync();
        return
        [
            .. cells.Select(c => (c.Key, c.State, c.CurrentVersion, StoredJson.Read<Shot>(versions.Single(v => v.Key == c.Key && v.Version == c.CurrentVersion).OutputJson)))
                .OrderBy(s => s.Item4.SegmentId).ThenBy(s => s.Item4.FirstWord),
        ];
    }

    private static async Task<StageState> StageAsync(IStoryForgeClient client, Guid projectId, PipelineStage stage, params StageState[] states) =>
        (await ScriptRunTests.WaitForAsync(
            () => client.GetProjectAsync(projectId),
            p => states.Contains(p.Stages.Single(s => s.Stage == stage).State))).Stages.Single(s => s.Stage == stage).State;

    [Fact]
    public async Task Picks_become_shot_keys_and_looks_are_stored()
    {
        var good = Good();
        var first = good.Segments[0];
        _board.Answer(good with
        {
            Segments = [first with { Shots = [first.Shots[0], first.Shots[1], first.Shots[2] with { EarlierStills = [new("Karlach", "S01-2")] }, .. first.Shots.Skip(3)] }, .. good.Segments.Skip(1)],
        });
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var shots = await ShotsAsync();
        Assert.Equal([new EarlierStill("Karlach", shots[1].Key)], shots[2].Shot.EarlierStills);
        var view = await client.GetStoryboardAsync(project.Id);
        Assert.Equal("The look of Karlach.", view.Cast.Single(c => c.Name == "Karlach").Look);
    }

    [Fact]
    public async Task The_shot_writer_gets_the_approved_facts()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        Assert.Equal(["F01", "F02", "F03"], _board.Requests.Single().Facts!.Select(f => f.Id));
    }

    [Fact]
    public async Task A_contradiction_goes_back_to_the_shot_writer()
    {
        _board.Answer(Good()).Answer(Good());
        _facts.Answer(new FactProblemOutput("S02-1", "F01", "F01 says one soul; this shows two."));
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        Assert.Equal(2, _board.Requests.Count);
        Assert.Equal(["S02-1 contradicts F01: F01 says one soul; this shows two."], _board.Problems[1]);
        Assert.All(await ShotsAsync(), s => Assert.Null(s.Shot.FactDisagreements));
    }

    [Fact]
    public async Task A_contradiction_about_a_shot_surviving_the_tries_reaches_the_gate_on_that_shot()
    {
        _board.Answer(Good()).Answer(Good()).Answer(Good());
        var problem = new FactProblemOutput("S02-1", "F01", "F01 says one soul; this shows two.");
        _facts.Answer(problem).Answer(problem).Answer(problem);
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        Assert.Equal(StageState.NeedsReview, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview, StageState.Failed));
        var shots = await ShotsAsync();
        var noted = shots.Single(s => s.Shot.FactDisagreements is { Count: > 0 });
        Assert.Equal(("S02", 0), (noted.Shot.SegmentId, noted.Shot.FirstWord));
        Assert.Equal(new FactDisagreement("S02-1", "F01", "F01 says one soul; this shows two."), noted.Shot.FactDisagreements!.Single());
        Assert.Empty((await client.GetStoryboardAsync(project.Id)).FactDisagreements!);
    }

    [Fact]
    public async Task A_contradiction_about_a_look_surviving_the_tries_reaches_the_gate_on_the_sheet()
    {
        _board.Answer(Good()).Answer(Good()).Answer(Good());
        var problem = new FactProblemOutput("Karlach", "F02", "F02 says she has no horns.");
        _facts.Answer(problem).Answer(problem).Answer(problem);
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        Assert.Equal(new FactDisagreement("Karlach", "F02", "F02 says she has no horns."), (await client.GetStoryboardAsync(project.Id)).FactDisagreements!.Single());
    }

    [Fact]
    public async Task An_unreadable_fact_check_reaches_the_gate_with_a_note()
    {
        _board.Answer(Good());
        _facts.AnswerNothing().AnswerNothing().AnswerNothing();
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        Assert.Equal(StageState.NeedsReview, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview, StageState.Failed));
        Assert.Single(_board.Requests);
        Assert.Equal(3, _facts.Requests.Count);
        Assert.Equal(new FactDisagreement("", "", "The facts could not be checked."), (await client.GetStoryboardAsync(project.Id)).FactDisagreements!.Single());
    }

    [Fact]
    public async Task The_check_reads_every_shot_of_a_whole_storyboard_and_the_looks()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var request = _facts.Requests.Single();
        Assert.Equal(60, request.Shots.Count);
        Assert.Equal(new CheckedShot("S02-1", "S02 shot 1"), request.Shots[15]);
        Assert.Equal("The look of Karlach.", request.Cast.Single(c => c.Name == "Karlach").Look);
        Assert.Equal(["F01", "F02", "F03"], request.Facts.Select(f => f.Id));
    }

    [Fact]
    public async Task Only_the_shot_just_written_again_is_checked_and_a_contradiction_goes_back()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var before = await ShotsAsync();
        _board.AnswerShot(new ShotRewriteOutput("Karlach has two heads.", ["Karlach"], true, [new("Karlach", "")]))
            .AnswerShot(new ShotRewriteOutput("Karlach turns the coin.", ["Karlach"], true, [new("Karlach", "")]));
        _facts.Answer(new FactProblemOutput("S01-2", "F02", "F02 says one head."));

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, before[1].Key);
        await ScriptRunTests.WaitForAsync(ShotsAsync, s => s.Single(x => x.Key == before[1].Key).Version == 2);

        Assert.All(_facts.Requests.Skip(1), r => Assert.Equal("S01-2", r.Shots.Single().Label));
        Assert.Equal("Karlach turns the coin.", (await ShotsAsync()).Single(x => x.Key == before[1].Key).Shot.Description);
    }

    [Fact]
    public async Task A_rewrite_still_contradicting_after_the_tries_carries_the_note_on_the_shot()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var before = await ShotsAsync();
        var shot = new ShotRewriteOutput("Karlach has two heads.", ["Karlach"], true, [new("Karlach", "")]);
        _board.AnswerShot(shot).AnswerShot(shot).AnswerShot(shot);
        var problem = new FactProblemOutput("S01-2", "F02", "F02 says one head.");
        _facts.Answer(problem).Answer(problem).Answer(problem);

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, before[1].Key);
        await ScriptRunTests.WaitForAsync(ShotsAsync, s => s.Single(x => x.Key == before[1].Key).Version == 2);

        Assert.Equal("F02", (await ShotsAsync()).Single(x => x.Key == before[1].Key).Shot.FactDisagreements!.Single().FactId);
    }

    [Fact]
    public void The_fact_check_is_told_a_look_you_typed_is_given()
    {
        var prompt = ClaudeCliStoryboardFactCheck.Prompt(new FactCheckRequest(
            Guid.NewGuid(),
            "",
            [new Fact("F01", "Nosulgor is blind.", "u", "q")],
            [new CastMember("Nosulgor", CastKind.Character, "", "One eye, huge.", LookByYou: true), new CastMember("Troll", CastKind.Character, "", "Grey.")],
            [new CheckedShot("S02-1", "Nosulgor sleeps.")]));

        Assert.Contains("F01: Nosulgor is blind.", prompt);
        Assert.Contains("Nosulgor (given, do not raise it): One eye, huge.", prompt);
        Assert.Contains("Troll: Grey.", prompt);
        Assert.Contains("S02-1: Nosulgor sleeps.", prompt);
    }

    [Fact]
    public async Task With_no_facts_nothing_is_asked()
    {
        var check = new CountingAsk();
        var request = new FactCheckRequest(Guid.NewGuid(), "", [], [], []);

        Assert.Empty(await StoryboardFacts.CheckAsync(check, request, new Progress<ActivityLine>(), default));
        Assert.Equal(0, check.Asked);
    }

    private sealed class CountingAsk : IStoryboardFactCheck
    {
        public int Asked { get; private set; }

        public Task<FactCheckOutput?> CheckAsync(FactCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult<FactCheckOutput?>(null);
        }
    }

    [Fact]
    public async Task The_facts_go_into_the_storyboards_hash()
    {
        var setup = await ProjectTests.ValidSetup(await StartAsync());
        var voices = new Dictionary<string, int> { ["S01"] = 1 };

        Assert.NotEqual(
            StoryboardStage.Hash(setup, voices, Pacing.Default, [new Fact("F01", "One soul.", "u", "q")]),
            StoryboardStage.Hash(setup, voices, Pacing.Default, [new Fact("F01", "Two souls.", "u", "q")]));
    }

    [Fact]
    public async Task An_approved_voice_starts_the_storyboard_and_it_waits_for_review()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        var state = await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview, StageState.Failed);
        await using (var db = await _engine.DbAsync())
        {
            Assert.Null((await db.Cells.SingleAsync(c => c.Stage == PipelineStage.Storyboard && c.Key == "")).Error);
        }
        Assert.Equal(StageState.NeedsReview, state);
        var shots = await ShotsAsync();
        Assert.Equal(60, shots.Count);
        Assert.All(shots, s => Assert.Equal((StageState.NeedsReview, 1), (s.State, s.Version)));
        Assert.Equal(15, shots.Count(s => s.Shot.SegmentId == "S01"));
        Assert.Equal(75, shots.Where(s => s.Shot.SegmentId == "S01").Sum(s => s.Shot.Seconds), 3);
        Assert.Equal("S01 shot 1", shots[0].Shot.Description);
        Assert.Equal(["Soul coin"], shots[0].Shot.Cast);
        // The model saw every segment's words with their timings.
        var request = _board.Requests.Single();
        Assert.Equal(["S01", "S02", "S03", "S04"], request.Segments.Select(s => s.Segment.Id));
        Assert.Equal(150, request.Segments[0].Words.Words.Count);
    }

    [Fact]
    public async Task Run_through_still_stops_at_the_storyboard()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client, s => s with { Mode = RunMode.RunThrough });

        Assert.Equal(StageState.NeedsReview, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview, StageState.Approved, StageState.Failed));
    }

    [Fact]
    public async Task A_bad_storyboard_goes_back_twice_then_the_stage_fails_with_the_last_problem()
    {
        var bad = Good() with { Segments = [.. Good().Segments.Take(3)] };
        _board.Answer(bad).Answer(bad).Answer(bad);
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);

        Assert.Equal(StageState.Failed, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.Failed, StageState.NeedsReview));
        Assert.Equal(3, _board.Requests.Count);
        Assert.Equal([[], ["S04 has no shots."], ["S04 has no shots."]], _board.Problems);
        await using var db = await _engine.DbAsync();
        var cell = await db.Cells.SingleAsync(c => c.Stage == PipelineStage.Storyboard && c.Key == "");
        Assert.Equal("The answer was not a valid storyboard after 3 tries. Last problem: S04 has no shots.", cell.Error);
    }

    [Fact]
    public async Task A_project_on_LM_Studio_says_the_storyboard_needs_Claude_CLI_for_now()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        await using (var db = await _engine.DbAsync())
        {
            var entry = await db.Projects.SingleAsync();
            var json = JsonNode.Parse(entry.SetupJson)!.AsObject();
            json["Script"]!["Provider"] = "LM Studio";
            entry.SetupJson = json.ToJsonString();
            await db.SaveChangesAsync();
        }

        await client.RegenerateAsync(project.Id, PipelineStage.Storyboard);

        Assert.Equal(StageState.Failed, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.Failed));
        await using var check = await _engine.DbAsync();
        Assert.Equal(
            "The storyboard is planned through Claude CLI for now, and this project uses LM Studio. LM Studio follows with #19.",
            (await check.Cells.SingleAsync(c => c.Stage == PipelineStage.Storyboard && c.Key == "")).Error);
    }

    [Fact]
    public async Task The_projects_pacing_goes_before_the_profiles()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client, s => s with { Pacing = new Pacing(2, 4, 6) });
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);

        Assert.Equal(new Pacing(2, 4, 6), _board.Requests.Single().Pacing);
        Assert.Equal(20, _board.Requests.Single().MaxClipSeconds);
    }

    [Fact]
    public async Task Without_its_own_pacing_a_project_uses_the_storyboard_profiles()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);

        Assert.Equal(Pacing.Default, _board.Requests.Single().Pacing);
    }

    [Fact]
    public async Task A_shot_written_again_answers_its_picks()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var before = await ShotsAsync();
        _board.AnswerShot(new ShotRewriteOutput("Karlach turns the coin.", ["Karlach"], false, [new("Karlach", "S01-2")]));

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, before[2].Key);
        await ScriptRunTests.WaitForAsync(ShotsAsync, s => s.Single(x => x.Key == before[2].Key).Version == 2);

        Assert.Equal([new EarlierStill("Karlach", before[1].Key)], (await ShotsAsync()).Single(x => x.Key == before[2].Key).Shot.EarlierStills);
    }

    [Fact]
    public async Task A_shot_written_again_keeps_its_timing_and_the_others_stay()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);
        var before = await ShotsAsync();
        _board.AnswerShot(new ShotRewriteOutput("Karlach turns the coin in her fingers.", ["karlach", "Soul coin"], true, [new("Soul coin", "")]));

        await client.RegenerateSegmentAsync(project.Id, PipelineStage.Storyboard, before[1].Key);
        await ScriptRunTests.WaitForAsync(ShotsAsync, s => s.Single(x => x.Key == before[1].Key).Version == 2);

        var after = await ShotsAsync();
        var shot = after.Single(s => s.Key == before[1].Key).Shot;
        Assert.Equal((before[1].Shot.FirstWord, before[1].Shot.WordCount, before[1].Shot.Start, before[1].Shot.End), (shot.FirstWord, shot.WordCount, shot.Start, shot.End));
        Assert.Equal(("Karlach turns the coin in her fingers.", true), (shot.Description, shot.ContinuesPrevious));
        Assert.Equal(["Karlach", "Soul coin"], shot.Cast);
        Assert.Equal("S01·2", _board.Rewrites.Single());
        Assert.All(after.Where(s => s.Key != before[1].Key), s => Assert.Equal(1, s.Version));
    }

    [Fact]
    public async Task Approving_every_shot_approves_the_storyboard_and_the_references_are_proposed()
    {
        _board.Answer(Good());
        var client = await StartAsync();
        var project = await VoiceApprovedAsync(client);
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview);

        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Storyboard);

        Assert.Equal(StageState.Approved, await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.Approved));
        Assert.All(await ShotsAsync(), s => Assert.Equal(StageState.Approved, s.State));
        // The test host's sources have no pictures: the list is proposed all the same, and waits for review.
        Assert.Equal(StageState.NeedsReview, await StageAsync(client, project.Id, PipelineStage.References, StageState.NeedsReview, StageState.Failed));
    }

    /// <summary>
    /// The voice without ComfyUI: every approved script segment "spoken" at once, a word every
    /// 0.5 s, each heard for 0.4 s, so a 150-word segment is 75 s long.
    /// </summary>
    internal sealed class WordsVoice(CellReader cells) : ISegmentWorker
    {
        public PipelineStage Stage => PipelineStage.Voice;

        public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
        {
            var segments = await cells.ApprovedSegmentsAsync(context.Project.Id, cancellationToken);
            var clips = segments.Select(s => Clip(s.Segment, s.Version)).ToList();
            // With the real stage's hash: what the voice is spoken from is what tells it out of date.
            return new StageResult(
                StoredJson.Write(new VoiceSheet([.. clips.Select(c => c.SegmentId)], clips.Sum(c => c.Seconds))),
                VoiceClip.SchemaVersion,
                "h",
                [.. segments.Zip(clips, (s, c) => new CellResult(c.SegmentId, StoredJson.Write(c), VoiceClip.SchemaVersion, VoiceStage.Hash(context.Project.Setup, s.Segment, s.Version)))]);
        }

        public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
        {
            var (segment, version) = (await cells.ApprovedSegmentsAsync(context.Project.Id, cancellationToken)).Single(s => s.Segment.Id == key);
            // Spoken again a little slower, so its words fall elsewhere.
            var clip = Clip(segment, version, 0.6);
            return new CellResult(key, StoredJson.Write(clip), VoiceClip.SchemaVersion, VoiceStage.Hash(context.Project.Setup, segment, version));
        }

        internal static VoiceClip Clip(Segment segment, int version, double every = 0.5)
        {
            var words = segment.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new VoiceClip(segment.Id, version, $"{segment.Id}.wav", words.Length * every, 1, [new VoicePart(segment.Narration, words.Length * every)],
                [.. words.Select((w, i) => new SpokenWord(w, i * every, i * every + 0.4))]);
        }
    }

    /// <summary>The fact check: no contradiction unless a test queues one; records what it was asked.</summary>
    internal sealed class FakeFactCheck : IStoryboardFactCheck
    {
        private readonly Queue<FactCheckOutput?> _answers = new();

        public List<FactCheckRequest> Requests { get; } = [];

        public FakeFactCheck Answer(params FactProblemOutput[] contradictions)
        {
            _answers.Enqueue(new FactCheckOutput(contradictions));
            return this;
        }

        /// <summary>An answer that could not be read.</summary>
        public FakeFactCheck AnswerNothing()
        {
            _answers.Enqueue(null);
            return this;
        }

        public Task<FactCheckOutput?> CheckAsync(FactCheckRequest request, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                return Task.FromResult(_answers.TryDequeue(out var answer) ? answer : new FactCheckOutput([]));
            }
        }
    }

    /// <summary>Storyboards and shots from a script; records what it was asked.</summary>
    internal sealed class FakeStoryboardAgent : IStoryboardAgent
    {
        private readonly Queue<Func<CancellationToken, Task<StoryboardOutput>>> _boards = new();
        private readonly Queue<Func<CancellationToken, Task<ShotRewriteOutput>>> _shots = new();

        public List<StoryboardRequest> Requests { get; } = [];

        public List<IReadOnlyList<string>> Problems { get; } = [];

        /// <summary>The shots written again, by their label: "S01·2".</summary>
        public List<string> Rewrites { get; } = [];

        public FakeStoryboardAgent Answer(StoryboardOutput output)
        {
            _boards.Enqueue(_ => Task.FromResult(output));
            return this;
        }

        /// <summary>The next storyboard comes once <paramref name="release"/> is done.</summary>
        public FakeStoryboardAgent AnswerWhen(Task release, StoryboardOutput output)
        {
            _boards.Enqueue(async token =>
            {
                await release.WaitAsync(token);
                return output;
            });
            return this;
        }

        public FakeStoryboardAgent AnswerShot(ShotRewriteOutput output)
        {
            _shots.Enqueue(_ => Task.FromResult(output));
            return this;
        }

        public void HoldShot() => _shots.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

        public async Task<ScriptAnswer<StoryboardOutput>> PlanAsync(StoryboardRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                Problems.Add(problems);
            }
            return new ScriptAnswer<StoryboardOutput>("s", await _boards.Dequeue()(cancellationToken));
        }

        public async Task<ScriptAnswer<ShotRewriteOutput>> RewriteShotAsync(StoryboardRequest request, IReadOnlyList<Shot> board, IReadOnlyList<CastMember> cast,
            string shotKey, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            var shot = board.Single(s => s.Key == shotKey);
            lock (Rewrites)
            {
                Rewrites.Add($"{shot.SegmentId}·{board.Where(s => s.SegmentId == shot.SegmentId).OrderBy(s => s.FirstWord).ToList().IndexOf(shot) + 1}");
            }
            return new ScriptAnswer<ShotRewriteOutput>("s", await _shots.Dequeue()(cancellationToken));
        }
    }

    [Fact]
    public async Task A_pacing_longer_than_the_projects_clips_is_held_to_them()
    {
        _board.Answer(Good());
        var client = await StartAsync();

        // The profile's 3–8 s, about 5 s, for clips of at most 6 s.
        var project = await VoiceApprovedAsync(client, s => s with { Clips = s.Clips with { MaxClipSeconds = 6 } });
        await StageAsync(client, project.Id, PipelineStage.Storyboard, StageState.NeedsReview, StageState.Failed);

        Assert.Equal((new Pacing(3, 5, 6), 6), (_board.Requests.Single().Pacing, _board.Requests.Single().MaxClipSeconds));
    }
}
