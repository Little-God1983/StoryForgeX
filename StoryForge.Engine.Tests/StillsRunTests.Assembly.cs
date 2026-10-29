using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Assembly;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Assembly stage in the pipeline, with a stand-in Claude and renderer: once every clip is
/// approved, Claude writes the cut list, it is checked against the clips' handles (and sent back
/// when it does not fit), and the video is rendered at the delivery size. Your cut list renders as it
/// is, without Claude; Regenerate drops it; a clip made again puts the video out of date.
/// </summary>
public sealed partial class StillsRunTests
{
    private readonly FakeCutLists _cuts = new();
    private readonly FakeRenderer _renderer = new();

    private Task<IStoryForgeClient> StartWithAssemblyAsync() => StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IClipPromptAgent>(_motion));
        services.Replace(ServiceDescriptor.Singleton<ICutListAgent>(_cuts));
        services.Replace(ServiceDescriptor.Singleton<IVideoRenderer>(_renderer));
        services.AddSingleton<IStageWorker, ClipsStage>();
        services.AddSingleton<IStageWorker, AssemblyStage>();
    });

    /// <summary>A project whose clips are all made and approved, so the video is cut; it waits at the Assembly gate.</summary>
    private async Task<Project> ClipsApprovedAsync(IStoryForgeClient client)
    {
        await File.WriteAllTextAsync(Path.Combine(_templates, "video.json"), VideoWorkflow);
        var video = await VideoProfileAsync(client, "Minimax H3 test");
        var project = await ReferencesApprovedAsync(client, s => s with
        {
            Clips = s.Clips with { Profile = video, Size = new GenerationSize("16:9", 1280, 704), MaxClipSeconds = 20 },
            Gates = [.. s.Gates.Where(g => g is not (PipelineStage.Clips or PipelineStage.Assembly)), PipelineStage.Clips, PipelineStage.Assembly],
        });
        await StillsAsync(client, project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        await ClipsAsync(client, project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Clips);
        return project;
    }

    private static Task<AssemblyView> AssemblyAsync(IStoryForgeClient client, Guid projectId, Func<AssemblyView, bool>? done = null) =>
        ScriptRunTests.WaitForAsync(() => client.GetAssemblyAsync(projectId), v => v.State == StageState.Failed || (done?.Invoke(v) ?? v.State is StageState.NeedsReview or StageState.Approved));

    [Fact]
    public async Task Approved_clips_are_cut_by_Claudes_list_and_rendered_at_the_delivery_size()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal((StageState.NeedsReview, 1), (view.State, view.Version));
        var made = view.Video!;
        Assert.Equal(new GenerationSize("16:9", 1920, 1080), made.Size);   // the delivery, not the clips' 1280 × 704
        Assert.Equal(59, made.CutList.Cuts.Count);
        Assert.Equal((CutTransition.Crossfade, 0.4, "on the words"), (made.CutList.Cuts[0].Transition, made.CutList.Cuts[0].Seconds, made.CutList.Cuts[0].Why));
        Assert.Equal(CutList.DefaultClipSoundDb, made.CutList.ClipSoundDb);
        Assert.Equal(60, made.Clips.Count);
        Assert.True(File.Exists(view.Path));
        Assert.Equal(60, view.Shots.Count);
        Assert.Equal(("S01·1", 0.5), (view.Shots[0].Label, view.Shots[0].Handle));
        // Claude saw every shot on the narration's timeline, once.
        var request = Assert.Single(_cuts.Requests).Request;
        Assert.Equal(60, request.Timeline.Shots.Count);
        Assert.Equal(4, request.Timeline.Narration.Count);
        // The render: every shot's piece, the frames adding up to the narration.
        var plan = Assert.Single(_renderer.Plans);
        Assert.Equal(60, plan.Pictures.Count);
        Assert.Equal((int)Math.Round(made.Seconds * 24, MidpointRounding.AwayFromZero), plan.Pictures.Sum(p => p.Frames) - plan.Joins.Sum(j => j.Frames));
        Assert.Equal(new GenerationSize("16:9", 1920, 1080), plan.Size);

        await client.ApproveAsync(project.Id, PipelineStage.Assembly, 1);
        Assert.Equal(StageState.Approved, (await client.GetAssemblyAsync(project.Id)).State);
    }

    [Fact]
    public async Task A_cut_list_that_does_not_fit_the_clips_is_sent_back_with_why()
    {
        var client = await StartWithAssemblyAsync();
        _cuts.Answers.Enqueue(r => new CutListOutput([.. FakeCutLists.Good(r).Cuts.Select((c, i) => i == 2 ? c with { Transition = "crossfade", Seconds = 1, Shift = 0.3 } : c)]));
        var project = await ClipsApprovedAsync(client);

        var view = await AssemblyAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(2, _cuts.Requests.Count);
        Assert.StartsWith("S01-3 → S01-4: a 1 s crossfade 0.3 s later needs 0.8 s of S01-3's clip after its words", Assert.Single(_cuts.Requests[1].Problems));
    }

    [Fact]
    public async Task Render_again_renders_your_cut_list_as_it_is_without_Claude()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;
        var mine = first with { Cuts = [.. first.Cuts.Select((c, i) => i == 1 ? c with { Transition = CutTransition.DipToBlack, Seconds = 0.6 } : c)], ClipSoundDb = -30 };

        await client.RenderCutListAsync(project.Id, mine);

        var view = await AssemblyAsync(client, project.Id, v => v.Version == 2 && v.State != StageState.Running);
        Assert.Null(view.Error);
        var cuts = view.Video!.CutList.Cuts;
        Assert.Equal((CutTransition.DipToBlack, true), (cuts[1].Transition, cuts[1].Yours));
        Assert.False(cuts[0].Yours);   // left as Claude cut it
        Assert.Equal(-30, view.Video.CutList.ClipSoundDb);
        Assert.Equal((VersionOrigin.Edited, 1), (view.Versions[1].Origin, view.Versions[1].BasedOn));
        Assert.Null(view.Waiting);
        Assert.Single(_cuts.Requests);   // Claude was not asked again
        Assert.Equal((CutTransition.DipToBlack, 14), _renderer.Plans[^1].Joins[1]);
    }

    [Fact]
    public async Task Your_edit_of_an_older_version_is_based_on_it_and_marks_only_what_you_changed_there()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;
        _cuts.Answers.Enqueue(r => new CutListOutput([.. FakeCutLists.Good(r).Cuts.Select((c, i) => i == 5 ? c with { Transition = "crossfade", Seconds = 0.4 } : c)]));
        await client.RegenerateAsync(project.Id, PipelineStage.Assembly);
        await AssemblyAsync(client, project.Id, v => v.Version == 2 && v.State != StageState.Running);

        // Back on v1, one cut moved: v2's crossfade is not in it, and is not yours either.
        await client.RenderCutListAsync(project.Id, first with { Cuts = [.. first.Cuts.Select((c, i) => i == 3 ? c with { Shift = 0.1 } : c)] }, editedFrom: 1);

        var view = await AssemblyAsync(client, project.Id, v => v.Version == 3 && v.State != StageState.Running);
        Assert.Equal(1, view.Video!.BasedOn);
        Assert.Equal([3], view.Video.CutList.Cuts.Select((c, i) => (c, i)).Where(x => x.c.Yours).Select(x => x.i));
    }

    [Fact]
    public async Task Cancel_while_your_cut_list_renders_leaves_nothing_waiting_to_render_unasked()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;
        _renderer.Gate = new TaskCompletionSource().Task;
        await client.RenderCutListAsync(project.Id, first with { ClipSoundDb = -12 });
        await AssemblyAsync(client, project.Id, v => v.State == StageState.Running && _renderer.Plans.Count == 2);

        await client.CancelAsync(project.Id, PipelineStage.Assembly);

        var view = await AssemblyAsync(client, project.Id, v => v.State != StageState.Running);
        Assert.Equal((StageState.NeedsReview, 1, (CutList?)null), (view.State, view.Version, view.Waiting));
    }

    [Fact]
    public async Task A_failed_render_says_which_version_your_waiting_cut_list_was_edited_from()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;
        _renderer.Failure = "ffmpeg could not render the video.";

        await client.RenderCutListAsync(project.Id, first with { ClipSoundDb = -12 }, editedFrom: 1);

        var failed = await AssemblyAsync(client, project.Id, v => v.State == StageState.Failed);
        Assert.Equal(1, failed.WaitingBasedOn);
    }

    [Fact]
    public async Task A_cut_of_yours_that_does_not_fit_is_refused_with_why()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RenderCutListAsync(project.Id, first with { Cuts = [.. first.Cuts.Select((c, i) => i == 1 ? c with { Shift = 0.6 } : c)] }));

        Assert.Equal("S01·2 → S01·3: the cut 0.6 s later needs 0.6 s of S01·2's clip after its words; it has 0.5 s.", ex.Message);
    }

    [Fact]
    public async Task A_failed_render_of_your_cut_list_keeps_it_waiting_and_Regenerate_asks_Claude_instead()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        var first = (await AssemblyAsync(client, project.Id)).Video!.CutList;
        _renderer.Failure = "ffmpeg could not render the video: No space left on device.";

        await client.RenderCutListAsync(project.Id, first with { ClipSoundDb = -12 });

        var failed = await AssemblyAsync(client, project.Id, v => v.State == StageState.Failed);
        Assert.Equal("ffmpeg could not render the video: No space left on device.", failed.Error);
        Assert.Equal(-12, failed.Waiting!.ClipSoundDb);
        _renderer.Failure = null;

        await client.RegenerateAsync(project.Id, PipelineStage.Assembly);

        var view = await AssemblyAsync(client, project.Id, v => v.Version == 2 && v.State != StageState.Running);
        Assert.Equal((2, (int?)null, (CutList?)null), (_cuts.Requests.Count, view.Video!.BasedOn, view.Waiting));
    }

    [Fact]
    public async Task A_clip_made_again_and_approved_puts_the_video_out_of_date()
    {
        var client = await StartWithAssemblyAsync();
        var project = await ClipsApprovedAsync(client);
        await AssemblyAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        Assert.Null((await client.GetAssemblyAsync(project.Id)).Stale);

        await client.QueueClipAsync(project.Id, keys["S02·1"]);
        await client.MakeClipsAsync(project.Id);
        await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S02·1"]).Version == 2);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Clips, keys["S02·1"], 2);

        var view = await client.GetAssemblyAsync(project.Id);
        Assert.Equal("S02·1's clip changed since the video was cut.", view.Stale);
        Assert.Equal(1, view.Version);   // not cut again on its own
    }

    internal sealed class FakeCutLists : ICutListAgent
    {
        public List<(CutListRequest Request, IReadOnlyList<string> Problems)> Requests { get; } = [];

        /// <summary>Answers to give first, in turn; then <see cref="Good"/>.</summary>
        public Queue<Func<CutListRequest, CutListOutput>> Answers { get; } = new();

        /// <summary>A crossfade after the first shot, hard cuts on the words after the rest.</summary>
        public static CutListOutput Good(CutListRequest request) =>
            new([.. request.Timeline.Shots.SkipLast(1).Select((s, i) => new CutOutput(StillPromptCheck.Name(s.Label), i == 0 ? "crossfade" : "cut", i == 0 ? 0.4 : 0, 0, 0, "on the words"))]);

        public Task<ScriptAnswer<CutListOutput>> WriteAsync(
            CutListRequest request, string? session, IReadOnlyList<string> problems, IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request, problems));
            }
            var answer = Answers.TryDequeue(out var next) ? next(request) : Good(request);
            return Task.FromResult(new ScriptAnswer<CutListOutput>("cut-session", answer));
        }
    }

    internal sealed class FakeRenderer : IVideoRenderer
    {
        public List<RenderPlan> Plans { get; } = [];

        /// <summary>While set, rendering fails with it.</summary>
        public string? Failure { get; set; }

        /// <summary>While set, rendering waits for it (or for Cancel).</summary>
        public Task? Gate { get; set; }

        public async Task RenderAsync(RenderPlan plan, string root, string output, int fps, IProgress<string> progress, CancellationToken cancellationToken)
        {
            lock (Plans)
            {
                Plans.Add(plan);
            }
            if (Failure is { } reason)
            {
                throw new StageFailedException(reason);
            }
            if (Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70], cancellationToken);
        }
    }
}
