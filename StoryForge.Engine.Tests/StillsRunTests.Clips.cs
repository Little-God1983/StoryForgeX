using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryForge.Client;
using StoryForge.Engine.Clips;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Script;
using StoryForge.Engine.Stills;

namespace StoryForge.Engine.Tests;

/// <summary>
/// The Clips stage in the pipeline, with stand-in prompts and ComfyUI: once every still is approved,
/// one clip per shot is made from its still, as long as the shot and its handles; a clip that fails
/// shows failed and the others are made; one clip can be made again, with your prompt or another
/// video profile, and the cell says which model made it.
/// </summary>
public sealed partial class StillsRunTests
{
    private readonly FakeClipPrompts _motion = new();

    /// <summary>An API-format image-to-video workflow: the still in, a prompt, a size, a frame count, a seed, a video out.</summary>
    private const string VideoWorkflow = """
        {
          "1": { "class_type": "LoadImage", "inputs": { "image": "" } },
          "2": { "class_type": "MiniMaxH3ImageToVideo", "inputs": { "prompt": "", "width": ["6", 0], "height": ["6", 1], "length": 124, "first_frame": ["3", 0] } },
          "3": { "class_type": "ImageScale", "inputs": { "image": ["1", 0], "width": 1344, "height": 768 } },
          "4": { "class_type": "RandomNoise", "inputs": { "noise_seed": 1 } },
          "5": { "class_type": "VHS_VideoCombine", "inputs": { "frame_rate": 24 } },
          "6": { "class_type": "GetImageSize", "inputs": { "image": ["3", 0] } }
        }
        """;

    private static readonly IReadOnlyList<WorkflowInput> VideoInputs =
    [
        new("prompt", "#2.prompt"), new("image", "#1.image"), new("seed", "#4.noise_seed"), new("width", "#3.width"), new("height", "#3.height"),
        new("frames", "#2.length"), new("fps", "#5.frame_rate"),
    ];

    private Task<IStoryForgeClient> StartWithClipsAsync() => StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton<IClipPromptAgent>(_motion));
        services.AddSingleton<IStageWorker, ClipsStage>();
    });

    /// <summary>A video profile on the test workflow, as the starter is: Claude's motion prompt in the template.</summary>
    private static async Task<ProfileRef> VideoProfileAsync(IStoryForgeClient client, string name, int maxClip = 20)
    {
        var profile = await client.CreateProfileAsync(ProfileKind.Video, name);
        var starter = await client.GetProfileVersionAsync(profile.Id);
        var saved = await client.SaveProfileVersionAsync(profile.Id, starter.Content with
        {
            WorkflowTemplate = "video.json",
            Inputs = VideoInputs,
            PromptTemplate = "{shot.motion}, cinematic",
            MaxClipSeconds = maxClip,
            Sizes = [new("16:9", 1280, 704)],
        });
        return new ProfileRef(profile.Id, saved.Version);
    }

    /// <summary>A project whose stills are all approved, so the clips start.</summary>
    private async Task<Project> StillsApprovedAsync(IStoryForgeClient client)
    {
        await File.WriteAllTextAsync(Path.Combine(_templates, "video.json"), VideoWorkflow);
        var video = await VideoProfileAsync(client, "Minimax H3 test");
        var project = await ReferencesApprovedAsync(client, s => s with
        {
            Clips = s.Clips with { Profile = video, Size = new GenerationSize("16:9", 1280, 704), MaxClipSeconds = 20 },
            Gates = [.. s.Gates, PipelineStage.Clips],   // each clip waits for review
        });
        await StillsAsync(client, project.Id);
        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Stills);
        return project;
    }

    private static Task<ClipsView> ClipsAsync(IStoryForgeClient client, Guid projectId, Func<ClipsView, bool>? done = null) =>
        ScriptRunTests.WaitForAsync(() => client.GetClipsAsync(projectId), v => v.State == StageState.Failed || (done?.Invoke(v) ?? v.State is StageState.NeedsReview or StageState.Approved));

    /// <summary>The clip runs (not the stills) in the order they ran.</summary>
    private List<JsonObject> ClipRuns()
    {
        lock (_comfy.Workflows)
        {
            return [.. _comfy.Workflows.Where(w => w.ToJsonString().Contains("VHS_VideoCombine", StringComparison.Ordinal))];
        }
    }

    private static string Motion(JsonObject workflow) => (string)workflow["2"]!["inputs"]!["prompt"]!;

    [Fact]
    public async Task Approved_stills_make_one_clip_per_shot_from_its_still_as_long_as_the_shot_and_its_handles()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        var stills = (await client.GetStillsAsync(project.Id)).Stills.ToDictionary(s => s.Key);

        var view = await ClipsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(60, view.Clips.Count);
        Assert.Equal("Minimax H3", view.Model);
        var keys = await KeysAsync(client, project.Id);
        var first = view.Clips.Single(c => c.Key == keys["S01·1"]);
        Assert.Equal((StageState.NeedsReview, 1), (first.State, first.Version));
        Assert.True(File.Exists(first.Path));
        Assert.EndsWith(".mp4", first.Path);
        var clip = first.Clip!;
        // Half a second before and after the shot.
        var shot = (await client.GetStoryboardAsync(project.Id)).Shots.Single(s => s.Key == keys["S01·1"]).Shot;
        Assert.Equal((Math.Round(shot.Seconds + 1, 3), 0.5), (clip.Seconds, clip.Handle));
        Assert.Equal(("Minimax H3", new GenerationSize("16:9", 1280, 704), stills[keys["S01·1"]].Version), (clip.Model, clip.Size, clip.StillVersion));
        Assert.Equal("the motion of S01-1, cinematic", clip.Prompt);

        var runs = ClipRuns();
        Assert.Equal(60, runs.Count);
        var run = runs.First(r => Motion(r).StartsWith("the motion of S01-1,", StringComparison.Ordinal));
        Assert.Equal("uploaded-" + Path.GetFileName(stills[keys["S01·1"]].Still!.File), (string)run["1"]!["inputs"]!["image"]!);
        Assert.Equal(((int)Math.Ceiling(clip.Seconds * 24), 24), ((int)run["2"]!["inputs"]!["length"]!, (int)run["5"]!["inputs"]!["frame_rate"]!));
        Assert.Equal((1280, 704), ((int)run["3"]!["inputs"]!["width"]!, (int)run["3"]!["inputs"]!["height"]!));
        // VRAM freed once before the video model loads; Claude wrote every motion prompt in one go, from the shot and its still.
        Assert.Equal(1, _comfy.Freed);
        var request = Assert.Single(_motion.Requests);
        Assert.Equal(60, request.Shots.Count);
        Assert.Equal(("S01 shot 1", "the still of S01-1, painted dark fantasy", clip.Seconds), (request.Shots[0].Description, request.Shots[0].Still, request.Shots[0].Seconds));
    }

    [Fact]
    public async Task A_clip_that_fails_shows_failed_with_the_reason_and_the_others_are_made()
    {
        var client = await StartWithClipsAsync();
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "the motion of S02-3,";
        var project = await StillsApprovedAsync(client);

        var view = await ClipsAsync(client, project.Id);

        var keys = await KeysAsync(client, project.Id);
        var failed = view.Clips.Single(c => c.Key == keys["S02·3"]);
        Assert.Equal((StageState.Failed, "S02·3: CUDA out of memory", 0), (failed.State, failed.Error, failed.Version));
        Assert.Equal(59, view.Clips.Count(c => c.Version == 1));
        Assert.Equal(StageState.NeedsReview, view.State);

        // Make clips tries the missing one again.
        _comfy.Failure = null;
        await client.MakeClipsAsync(project.Id);
        var again = await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S02·3"]).Version == 1);
        Assert.Equal(StageState.NeedsReview, again.Clips.Single(c => c.Key == keys["S02·3"]).State);
    }

    [Fact]
    public async Task One_clip_can_be_made_with_another_video_profile_and_the_cell_says_which_model_made_it()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var ltx = await VideoProfileAsync(client, "LTX 2.5", maxClip: 30);
        var keys = await KeysAsync(client, project.Id);
        Assert.Equal(new ClipModel(ltx, "LTX 2.5"), (await client.GetClipsAsync(project.Id)).Others[^1]);

        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: ltx);
        Assert.Equal((true, ltx), (await client.GetClipsAsync(project.Id)).Clips.Single(c => c.Key == keys["S03·2"]) is var waiting ? (waiting.Queued, waiting.QueuedProfile) : default);
        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S03·2"]).Version == 2);
        var clip = view.Clips.Single(c => c.Key == keys["S03·2"]);
        Assert.Equal(("LTX 2.5", false, (ProfileRef?)null), (clip.Clip!.Model, clip.Queued, clip.QueuedProfile));
        Assert.Equal("Minimax H3", view.Clips.Single(c => c.Key == keys["S03·3"]).Clip!.Model);   // nothing else switched
        Assert.Equal(61, ClipRuns().Count);
    }

    [Fact]
    public async Task Edit_prompt_makes_the_clip_again_with_your_prompt_as_it_is()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueClipAsync(project.Id, keys["S01·2"], "The coin spins once and settles.");
        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S01·2"]).Version == 2);
        var clip = view.Clips.Single(c => c.Key == keys["S01·2"]).Clip!;
        Assert.Equal(("The coin spins once and settles.", true), (clip.Prompt, clip.YourPrompt));
        Assert.Single(_motion.Requests);   // nothing more for Claude to write
    }

    [Fact]
    public async Task A_still_made_again_and_approved_puts_its_clip_out_of_date()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.QueueStillAsync(project.Id, keys["S04·5"]);
        await client.ContinuePipelineAsync(project.Id);
        await StillsAsync(client, project.Id, v => v.State != StageState.Running && v.Stills.Single(s => s.Key == keys["S04·5"]).Version == 2);
        await client.ApproveSegmentAsync(project.Id, PipelineStage.Stills, keys["S04·5"], 2);

        var clips = (await client.GetClipsAsync(project.Id)).Clips;
        Assert.Equal("The still of S04·5 changed since the clip was made.", clips.Single(c => c.Key == keys["S04·5"]).Stale);
        // S04·6's still starts from S04·5's: it is out of date, and so is its clip. Nothing else is.
        Assert.Equal("The still of S04·6 is out of date.", clips.Single(c => c.Key == keys["S04·6"]).Stale);
        Assert.Equal(2, clips.Count(c => c.Stale is not null));
    }

    [Fact]
    public async Task The_clips_wait_until_every_still_is_approved()
    {
        var client = await StartWithClipsAsync();
        await File.WriteAllTextAsync(Path.Combine(_templates, "video.json"), VideoWorkflow);
        var project = await ReferencesApprovedAsync(client);
        await StillsAsync(client, project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.MakeClipsAsync(project.Id));

        Assert.Equal("Approve every still first: the clips are made from them.", refused.Message);
        Assert.Empty(ClipRuns());
    }

    [Fact]
    public async Task Approve_remaining_leaves_a_clip_that_was_never_made_failed()
    {
        var client = await StartWithClipsAsync();
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "the motion of S02-3,";
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);

        await client.ApproveSegmentsAsync(project.Id, PipelineStage.Clips);

        var view = await client.GetClipsAsync(project.Id);
        Assert.Equal((StageState.Failed, "S02·3: CUDA out of memory"), view.Clips.Single(c => c.Key == keys["S02·3"]) is var c ? (c.State, c.Error) : default);
        Assert.Equal(59, view.Clips.Count(c => c.State == StageState.Approved));
    }

    [Fact]
    public async Task Taking_a_failed_clip_out_of_the_waiting_ones_gives_back_the_one_it_had()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "the motion of S01-2,";
        await client.QueueClipAsync(project.Id, keys["S01·2"]);
        await client.MakeClipsAsync(project.Id);
        await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S01·2"]).State == StageState.Failed);

        await client.UnqueueClipAsync(project.Id, keys["S01·2"]);

        var clip = (await client.GetClipsAsync(project.Id)).Clips.Single(c => c.Key == keys["S01·2"]);
        Assert.Equal((StageState.NeedsReview, 1, false, (string?)null), (clip.State, clip.Version, clip.Queued, clip.Error));
    }

    [Fact]
    public async Task A_clip_switched_to_a_profile_that_cannot_make_it_fails_alone()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var broken = await client.CreateProfileAsync(ProfileKind.Video, "Broken");
        var starter = await client.GetProfileVersionAsync(broken.Id);
        var saved = await client.SaveProfileVersionAsync(broken.Id, starter.Content with { WorkflowTemplate = "video.json", Inputs = [.. VideoInputs.Where(i => i.Key != "frames")] });
        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: new ProfileRef(broken.Id, saved.Version));
        await client.QueueClipAsync(project.Id, keys["S01·2"]);

        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S01·2"]).Version == 2);
        Assert.Equal(StageState.NeedsReview, view.State);
        var failed = view.Clips.Single(c => c.Key == keys["S03·2"]);
        Assert.Equal((StageState.Failed, 1), (failed.State, failed.Version));
        Assert.Equal("S03·2: The video profile Broken does not say where 'frames' goes. Map it to the workflow's node in the profile.", failed.Error);
    }

    [Fact]
    public async Task A_shot_longer_than_the_profile_switched_to_makes_fails_with_why()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var shorter = await VideoProfileAsync(client, "Short", maxClip: 4);
        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: shorter);

        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running);
        var shot = (await client.GetStoryboardAsync(project.Id)).Shots.Single(s => s.Key == keys["S03·2"]).Shot;
        var failed = view.Clips.Single(c => c.Key == keys["S03·2"]);
        Assert.Equal(StageState.Failed, failed.State);
        Assert.Equal($"S03·2: the shot lasts {shot.Seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} s, and Short makes at most 4 s. Make it with the project's model.", failed.Error);
        Assert.Equal(60, ClipRuns().Count);   // nothing sent to ComfyUI for it
    }

    [Fact]
    public async Task When_ComfyUI_will_not_free_its_memory_the_clips_are_made_all_the_same()
    {
        var client = await StartWithClipsAsync();
        _comfy.FreeFailure = "ComfyUI would not free its memory (HTTP 404).";
        var project = await StillsApprovedAsync(client);

        var view = await ClipsAsync(client, project.Id);

        Assert.Null(view.Error);
        Assert.Equal(60, view.Clips.Count(c => c.Version == 1));
        Assert.Contains(view.Activity, l => l.Text.Contains("could not free ComfyUI's VRAM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_switched_clip_can_be_switched_back_to_the_projects_model()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        var view = await ClipsAsync(client, project.Id);
        var ltx = await VideoProfileAsync(client, "LTX 2.5", maxClip: 30);
        var keys = await KeysAsync(client, project.Id);
        var own = (await client.GetProjectAsync(project.Id)).Setup.Clips.Profile;
        Assert.Equal(["Minimax H3", "LTX 2.5"], (await client.GetClipsAsync(project.Id)).Others.Select(m => m.Name));   // the project's first

        await client.QueueClipAsync(project.Id, keys["S03·2"], "Slow pan.", profile: ltx);
        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: own);

        var clip = (await client.GetClipsAsync(project.Id)).Clips.Single(c => c.Key == keys["S03·2"]);
        Assert.Equal((true, (ProfileRef?)null, "Slow pan."), (clip.Queued, clip.QueuedProfile, clip.QueuedPrompt));
    }

    [Fact]
    public async Task Approving_a_clip_whose_making_again_failed_takes_it_out_of_the_waiting_ones()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        _comfy.Failure = "CUDA out of memory";
        _comfy.FailFor = "the motion of S01-2,";
        await client.QueueClipAsync(project.Id, keys["S01·2"]);
        await client.MakeClipsAsync(project.Id);
        await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S01·2"]).State == StageState.Failed);

        await client.ApproveSegmentAsync(project.Id, PipelineStage.Clips, keys["S01·2"], 1);

        var clip = (await client.GetClipsAsync(project.Id)).Clips.Single(c => c.Key == keys["S01·2"]);
        Assert.Equal((StageState.Approved, false), (clip.State, clip.Queued));
    }

    [Fact]
    public async Task Cancelled_after_only_failed_clips_the_stage_says_what_failed_not_what_was_kept()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var shorter = await VideoProfileAsync(client, "Short", maxClip: 4);
        await client.QueueClipAsync(project.Id, keys["S01·2"]);
        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: shorter);
        _comfy.Hold = new TaskCompletionSource();
        _comfy.HoldFor = "the motion of S01-2,";
        await client.MakeClipsAsync(project.Id);
        await ScriptRunTests.WaitForAsync(() => client.GetClipsAsync(project.Id), v => v.Clips.Single(c => c.Key == keys["S03·2"]).State == StageState.Failed);

        await client.CancelAsync(project.Id, PipelineStage.Clips);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running);
        Assert.Equal((StageState.NeedsReview, (string?)null), (view.State, view.Error));
        Assert.Equal(StageState.Failed, view.Clips.Single(c => c.Key == keys["S03·2"]).State);
    }

    [Fact]
    public async Task A_new_video_profile_lists_what_the_clips_stage_fills()
    {
        var client = await StartAsync();

        var profile = await client.CreateProfileAsync(ProfileKind.Video, "Fresh");
        var keys = (await client.GetProfileVersionAsync(profile.Id)).Content.Inputs.Select(i => i.Key).ToList();

        Assert.Contains("image", keys);
        Assert.Contains("frames", keys);
        Assert.Contains("fps", keys);
    }

    [Fact]
    public async Task When_Claude_cannot_write_the_prompts_a_clip_with_your_prompt_is_made_all_the_same()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await client.QueueClipAsync(project.Id, keys["S01·2"], "The coin spins.");
        await client.QueueClipAsync(project.Id, keys["S01·3"]);
        _motion.Failure = "Claude CLI is not reachable.";

        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S01·2"]).Version == 2);
        var failed = view.Clips.Single(c => c.Key == keys["S01·3"]);
        Assert.Equal((StageState.Failed, "S01·3: Claude CLI is not reachable."), (failed.State, failed.Error));
    }

    [Fact]
    public async Task VRAM_is_freed_again_before_another_video_model_loads()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        var ltx = await VideoProfileAsync(client, "LTX 2.5", maxClip: 30);
        await client.QueueClipAsync(project.Id, keys["S01·2"]);
        await client.QueueClipAsync(project.Id, keys["S03·2"], profile: ltx);
        var before = _comfy.Freed;

        await client.MakeClipsAsync(project.Id);

        await ClipsAsync(client, project.Id, v => v.State != StageState.Running && v.Clips.Single(c => c.Key == keys["S03·2"]).Version == 2);
        Assert.Equal(before + 2, _comfy.Freed);
    }

    [Fact]
    public async Task A_shot_too_long_for_the_projects_own_model_says_so_without_sending_you_back_to_it()
    {
        var client = await StartWithClipsAsync();
        var project = await StillsApprovedAsync(client);
        await ClipsAsync(client, project.Id);
        var keys = await KeysAsync(client, project.Id);
        await using (var db = await _engine.DbAsync())
        {
            var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Projects, p => p.Id == project.Id);
            var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var setup = System.Text.Json.JsonSerializer.Deserialize<ProjectSetup>(entry.SetupJson, options)!;
            entry.SetupJson = System.Text.Json.JsonSerializer.Serialize(setup with { Clips = setup.Clips with { MaxClipSeconds = 4 } }, options);
            await db.SaveChangesAsync();
        }
        await client.QueueClipAsync(project.Id, keys["S01·2"]);

        await client.MakeClipsAsync(project.Id);

        var view = await ClipsAsync(client, project.Id, v => v.State != StageState.Running);
        var error = view.Clips.Single(c => c.Key == keys["S01·2"]).Error ?? view.Error;
        Assert.EndsWith("Minimax H3 makes at most 4 s. Cut the shot shorter on the storyboard.", error);
    }

    [Theory]
    [InlineData(5, 20, 6, 0.5)]
    [InlineData(19.6, 20, 20, 0.2)]
    [InlineData(20, 20, 20, 0)]
    public void A_clip_runs_half_a_second_before_and_after_its_shot_but_never_past_the_longest_clip(double shot, int max, double seconds, double handle) =>
        Assert.Equal((seconds, handle), ClipsStage.Length(shot, max));

    internal sealed class FakeClipPrompts : IClipPromptAgent
    {
        public List<ClipPromptRequest> Requests { get; } = [];

        /// <summary>While set, asking fails with it, as Claude CLI down would.</summary>
        public string? Failure { get; set; }

        public Task<ScriptAnswer<StillPromptsOutput>> WriteAsync(ClipPromptRequest request, string? session, IReadOnlyList<string> problems,
            IProgress<ActivityLine> activity, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            if (Failure is { } reason)
            {
                throw new StageFailedException(reason);
            }
            var prompts = request.Shots.Select(s => StillPromptCheck.Name(s.Label)).Select(label => new StillPromptOutput(label, $"the motion of {label}")).ToList();
            return Task.FromResult(new ScriptAnswer<StillPromptsOutput>("s", new StillPromptsOutput(prompts)));
        }
    }
}
