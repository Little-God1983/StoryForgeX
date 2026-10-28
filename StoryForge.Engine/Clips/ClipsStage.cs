using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;
using StoryForge.Engine.Research;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Stills;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Clips;

/// <summary>The Clips stage's whole result: which shots got a clip.</summary>
internal sealed record ClipsSheet(IReadOnlyList<string> ShotKeys);

/// <summary>
/// The Clips stage: one clip per shot, made through the video profile's ComfyUI image-to-video
/// workflow from the shot's approved still (its first frame), resized to the video size. Claude
/// writes each shot's motion prompt from the storyboard, the still and the profile's instructions;
/// the profile's template wraps it. A clip runs as long as its shot and half a second before and
/// after it, for the cuts (less when the shot is near the model's longest clip). Each clip is a
/// cell of its own, keyed by its shot.
/// A run makes what is waiting: every shot with no clip yet, and every clip queued again
/// (Regenerate, Edit prompt…, Make with…), in shot order and each video profile's together, so a
/// model loads once. A clip switched to another profile (the fallback, e.g. LTX 2.5) is made with
/// it; nothing is ever switched without you.
/// </summary>
internal sealed class ClipsStage(
    IComfyUi comfy,
    IClipPromptAgent agent,
    CellReader cells,
    ProfileStore profiles,
    SettingsStore settings,
    ProjectFolders folders,
    TimeProvider clock) : ISegmentWorker
{
    /// <summary>The frame rate the clips are made at, and the frame count is counted in.</summary>
    public const int FramesPerSecond = 24;

    /// <summary>The seconds a clip runs before and after its shot.</summary>
    public const double Handle = 0.5;

    /// <summary>The profile input keys the stage fills besides the image ones: the frame count and the frame rate.</summary>
    public const string FramesKey = "frames";
    public const string FpsKey = "fps";

    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    public PipelineStage Stage => PipelineStage.Clips;

    public string CancelledAfter(int stored) =>
        $"Cancelled after {stored} {(stored == 1 ? "clip" : "clips")}. Those are kept; Make clips makes the ones still waiting.";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var stored = await cells.CurrentClipsAsync(context.Project.Id, cancellationToken);
        var queued = await cells.QueuedClipsAsync(context.Project.Id, cancellationToken);
        var todo = plan.Shots.Where(s => !stored.ContainsKey(s.Key) || queued.ContainsKey(s.Key)).ToList();
        if (todo.Count == 0)
        {
            Report(context, ActivityKind.Check, "every shot has its clip: nothing was waiting");
            return new StageResult(StoredJson.Write(new ClipsSheet([])), Clip.SchemaVersion, InputHash.Of(Array.Empty<string>()));
        }
        // Each video profile's clips together, the project's first: a model loads once.
        var own = context.Project.Setup.Clips.Profile;
        var groups = todo.GroupBy(s => queued.GetValueOrDefault(s.Key).Profile ?? own).OrderBy(g => g.Key == own ? 0 : 1).ToList();
        var animators = new Dictionary<ProfileRef, Animator>();
        foreach (var group in groups)
        {
            // Before Claude is asked: a profile that cannot make the clips fails at once.
            animators[group.Key] = await AnimatorAsync(context, group.Key, cancellationToken);
        }
        var asked = todo.Where(s => queued.GetValueOrDefault(s.Key).Prompt is null).ToList();
        var prompts = asked.Count > 0
            ? await PromptsAsync(context, plan, [.. asked.Select(s => (s, animators[queued.GetValueOrDefault(s.Key).Profile ?? own]))], cancellationToken)
            : new Dictionary<string, string>();
        await FreeAsync(context, cancellationToken);

        var results = new List<CellResult>();
        var made = new List<string>();
        var failures = new List<string>();
        foreach (var group in groups)
        {
            var animator = animators[group.Key];
            foreach (var shot in group)
            {
                var label = plan.Labels[shot.Key];
                Report(context, ActivityKind.Model, $"{label} ({results.Count + 1} of {todo.Count}) · {animator.Model}");
                var yours = queued.GetValueOrDefault(shot.Key).Prompt;
                var prompt = yours ?? StillsStage.Compose(animator.Profile.PromptTemplate, prompts[label], []);
                Clip clip;
                try
                {
                    clip = await AnimateAsync(context, animator, plan, shot, prompt, yours is not null, cancellationToken);
                }
                catch (StageFailedException ex) when (context.FailSegment is { } fail)
                {
                    // That clip shows failed, with the reason: Regenerate, or make it with another model.
                    // The others are made all the same.
                    Report(context, ActivityKind.Failed, ex.Message);
                    await fail(shot.Key, ex.Message);
                    failures.Add(ex.Message);
                    continue;
                }
                var result = new CellResult(shot.Key, StoredJson.Write(clip), Clip.SchemaVersion, Hash(context.Project.Setup, shot, clip, group.Key, yours));
                // Stored at once: a clip takes a while, and you watch it while the next is made.
                if (context.StoreSegment is { } store)
                {
                    await store(result);
                }
                results.Add(result);
                made.Add(shot.Key);
            }
        }
        if (results.Count == 0)
        {
            throw new StageFailedException(failures.Count == 1 ? failures[0] : $"No clip could be made. The first: {failures[0]}");
        }
        Report(context, ActivityKind.Check, $"{results.Count} {(results.Count == 1 ? "clip" : "clips")} made{(failures.Count > 0 ? $", {failures.Count} failed" : "")}");
        return new StageResult(StoredJson.Write(new ClipsSheet(made)), Clip.SchemaVersion, InputHash.Of(results.Select(r => r.InputHash)));
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var shot = plan.Shots.FirstOrDefault(s => s.Key == key) ?? throw new StageFailedException($"The storyboard has no shot {key}.");
        var (yours, switched) = (await cells.QueuedClipsAsync(context.Project.Id, cancellationToken)).GetValueOrDefault(key);
        var profile = switched ?? context.Project.Setup.Clips.Profile;
        var animator = await AnimatorAsync(context, profile, cancellationToken);
        var prompt = yours ?? StillsStage.Compose(animator.Profile.PromptTemplate, (await PromptsAsync(context, plan, [(shot, animator)], cancellationToken))[plan.Labels[key]], []);
        await FreeAsync(context, cancellationToken);
        var clip = await AnimateAsync(context, animator, plan, shot, prompt, yours is not null, cancellationToken);
        return new CellResult(key, StoredJson.Write(clip), Clip.SchemaVersion, Hash(context.Project.Setup, shot, clip, profile, yours));
    }

    /// <summary>Everything a clip is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, Shot shot, Clip clip, ProfileRef profile, string? yourPrompt) => InputHash.Of(new
    {
        stage = PipelineStage.Clips,
        schema = Clip.SchemaVersion,
        shot.Description,
        shot.Start,
        shot.End,
        still = clip.StillVersion,
        yourPrompt,
        writer = new { setup.Script.Provider, setup.Script.Model },
        provider = setup.Clips.Provider,
        profile,
        size = clip.Size,
    });

    /// <summary>
    /// How long a shot's clip runs, and its handles: half a second before and after, less when the
    /// shot is near the model's longest clip, so the clip never runs past it.
    /// </summary>
    internal static (double Seconds, double Handle) Length(double shotSeconds, int maxClipSeconds)
    {
        var handle = Math.Clamp((maxClipSeconds - shotSeconds) / 2, 0, Handle);
        return (Math.Round(shotSeconds + 2 * handle, 3), Math.Round(handle, 3));
    }

    /// <summary>What every clip of a run is made from: the shots, their labels and words, their approved stills.</summary>
    private sealed record Plan(
        IReadOnlyList<Shot> Shots,
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyDictionary<string, string> Heard,
        IReadOnlyDictionary<string, (Still Still, int Version)> Stills,
        string Root);

    private async Task<Plan> PlanAsync(StageContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        if (await cells.StateAsync(project.Id, PipelineStage.Stills, cancellationToken) != StageState.Approved)
        {
            throw new StageFailedException("Approve every still first: the clips are made from them.");
        }
        var shots = await cells.CurrentShotsAsync(project.Id, cancellationToken);
        if (shots.Count == 0)
        {
            throw new StageFailedException("The storyboard has no shots yet.");
        }
        var stills = await cells.ApprovedStillsAsync(project.Id, cancellationToken);
        var heard = new Dictionary<string, string>();
        var board = await cells.CurrentStoryboardAsync(project.Id, cancellationToken);
        foreach (var segment in shots.Select(s => s.SegmentId).Distinct())
        {
            var words = board?.VoiceVersions.TryGetValue(segment, out var version) == true
                ? (await cells.VoiceAsync(project.Id, segment, version, cancellationToken))?.Words ?? []
                : [];
            foreach (var shot in shots.Where(s => s.SegmentId == segment))
            {
                heard[shot.Key] = string.Join(' ', words.Skip(shot.FirstWord).Take(shot.WordCount).Select(w => w.Text));
            }
        }
        return new Plan(shots, shots.ToDictionary(s => s.Key, s => ShotTiming.Label(shots, s.Key)), heard, stills, await folders.RootAsync(cancellationToken));
    }

    /// <summary>Claude's motion prompt for each shot, by label; a failed check goes back to it, twice.</summary>
    private async Task<IReadOnlyDictionary<string, string>> PromptsAsync(
        StageContext context, Plan plan, IReadOnlyList<(Shot Shot, Animator Animator)> shots, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Script.Provider != ResearchStage.ClaudeCli)
        {
            throw new StageFailedException(
                $"The motion prompts are written through Claude CLI for now, and this project uses {setup.Script.Provider}. LM Studio follows with #19.");
        }
        var labels = shots.Select(s => plan.Labels[s.Shot.Key]).ToList();
        var request = new ClipPromptRequest(
            context.Project.Id,
            setup.Brief,
            [.. shots.Select(s => new ClipShot(
                plan.Labels[s.Shot.Key], s.Shot.Description, plan.Heard.GetValueOrDefault(s.Shot.Key, ""),
                plan.Stills.TryGetValue(s.Shot.Key, out var still) ? still.Still.Prompt : "",
                Length(s.Shot.Seconds, s.Animator.MaxClipSeconds).Seconds))],
            // The project's profile says how to write them; a clip switched to another is written the same way.
            (await profiles.GetVersionAsync(setup.Clips.Profile.ProfileId, setup.Clips.Profile.Version, cancellationToken)).Content.Instructions,
            setup.Script.Model);
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.WriteAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = StillPromptCheck.Problems(answer.Output, labels);
            if (problems.Count == 0)
            {
                return StillPromptCheck.ByLabel(answer.Output!, labels);
            }
            var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
            var last = attempt == Tries || session is null;
            Report(context, ActivityKind.Check, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException($"The answer had no good motion prompts after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>What a clip is made with: a video profile's workflow, its size, its longest clip, and what to call it.</summary>
    /// <param name="Model">"Minimax H3" for the project's profile; the profile's name for one a clip was switched to.</param>
    /// <param name="Uploaded">The stills uploaded to ComfyUI in this run, with the name it gave each: every one goes once.</param>
    private sealed record Animator(
        JsonObject Workflow, ProfileContent Profile, string Model, GenerationSize Size, int MaxClipSeconds, string Root, string Folder, Dictionary<string, string> Uploaded);

    private async Task<Animator> AnimatorAsync(StageContext context, ProfileRef profileRef, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (!string.Equals(setup.Clips.Provider, VoiceStage.ComfyUi, StringComparison.OrdinalIgnoreCase))
        {
            throw new StageFailedException($"Clips are made through ComfyUI for now, and this project uses {setup.Clips.Provider}.");
        }
        var own = profileRef == setup.Clips.Profile;
        var profile = (await profiles.GetVersionAsync(profileRef.ProfileId, profileRef.Version, cancellationToken)).Content;
        var name = own ? setup.Clips.Model : (await profiles.ListAsync(cancellationToken)).FirstOrDefault(p => p.Id == profileRef.ProfileId)?.Name ?? "another video profile";
        var templates = (await settings.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
        var workflow = await WorkflowTemplate.LoadAsync(templates, profile.WorkflowTemplate, cancellationToken, "video");
        foreach (var key in new[] { ReferencesStage.PromptKey, ReferencesStage.ImageKey, FramesKey })
        {
            if (!WorkflowTemplate.Maps(profile.Inputs, key))
            {
                throw new StageFailedException($"The video profile{(own ? "" : $" {name}")} does not say where '{key}' goes. Map it to the workflow's node in the profile.");
            }
        }
        // The project's video size; a profile switched to makes the same shape in its own size.
        var size = own ? setup.Clips.Size : profile.Sizes.FirstOrDefault(s => s.Aspect == setup.Clips.Size.Aspect) ?? setup.Clips.Size;
        var max = own ? setup.Clips.MaxClipSeconds : profile.MaxClipSeconds ?? setup.Clips.MaxClipSeconds;
        return new Animator(workflow, profile, name, size, max, await folders.RootAsync(cancellationToken), ProjectFolders.Of(context.Project), []);
    }

    /// <summary>Frees ComfyUI's VRAM before the video model loads, as Settings say (the image model may still hold it).</summary>
    private async Task FreeAsync(StageContext context, CancellationToken cancellationToken)
    {
        if ((await settings.LoadAsync(cancellationToken)).ComfyUi.FreeVramBetweenStages)
        {
            Report(context, ActivityKind.Model, "freeing ComfyUI's VRAM");
            await comfy.FreeMemoryAsync(cancellationToken);
        }
    }

    /// <summary>One ComfyUI run; the clip is saved as clips/&lt;shot&gt;-&lt;take&gt; in the project folder.</summary>
    private async Task<Clip> AnimateAsync(StageContext context, Animator animator, Plan plan, Shot shot, string prompt, bool yours, CancellationToken cancellationToken)
    {
        var label = plan.Labels[shot.Key];
        try
        {
            if (!plan.Stills.TryGetValue(shot.Key, out var still) || !File.Exists(Path.Combine(animator.Root, still.Still.File)))
            {
                throw new StageFailedException("its approved still is gone from the projects folder. Make the still again first.");
            }
            if (!animator.Uploaded.TryGetValue(still.Still.File, out var uploaded))
            {
                animator.Uploaded[still.Still.File] = uploaded = await comfy.UploadAsync(Path.Combine(animator.Root, still.Still.File), cancellationToken);
            }
            var (seconds, handle) = Length(shot.Seconds, animator.MaxClipSeconds);
            var seed = Random.Shared.Next();
            var values = new Dictionary<string, JsonNode>
            {
                [ReferencesStage.PromptKey] = prompt,
                [ReferencesStage.SeedKey] = seed,
                [ReferencesStage.WidthKey] = animator.Size.Width,
                [ReferencesStage.HeightKey] = animator.Size.Height,
                [ReferencesStage.ImageKey] = uploaded,
                [FramesKey] = (int)Math.Ceiling(seconds * FramesPerSecond),
                [FpsKey] = FramesPerSecond,
            };
            if (animator.Profile.NegativePrompt.Trim() is { Length: > 0 } negative)
            {
                values[StillsStage.NegativeKey] = negative;
            }
            if (animator.Profile.Steps is { } steps)
            {
                values[StillsStage.StepsKey] = steps;
            }
            var made = await comfy.RunAsync(
                WorkflowTemplate.Apply(animator.Workflow, animator.Profile.Inputs, values),
                $"StoryForge · {context.Project.Setup.Name} · clip {label}",
                new Progress(line => Report(context, ActivityKind.Model, $"{label}: {line}")),
                cancellationToken);
            // The video with its sound first: a combine node with audio saves that one under its own name.
            var videos = made.Where(f => Path.GetExtension(f.Name).ToLowerInvariant() is ".mp4" or ".webm" or ".mov" or ".mkv").ToList();
            var video = videos.FirstOrDefault(f => f.Name.Contains("-audio", StringComparison.OrdinalIgnoreCase)) ?? videos.FirstOrDefault()
                ?? throw new StageFailedException("The workflow saved no video. It needs a node that saves it, e.g. Video Combine.");
            var file = Path.Combine(animator.Folder, "clips", $"{StillsStage.FileName(shot.Key)}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(video.Name).ToLowerInvariant()}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(animator.Root, file))!);
            await File.WriteAllBytesAsync(Path.Combine(animator.Root, file), video.Content, cancellationToken);
            Report(context, ActivityKind.Check, $"{label} made · {seconds:0.##} s · {animator.Model}");
            return new Clip(shot.Key, file, prompt, seed, animator.Size, seconds, handle, animator.Model, animator.Profile.WorkflowTemplate, still.Version, yours);
        }
        catch (StageFailedException ex)
        {
            throw new StageFailedException($"{label}: {ex.Message}");   // the bar says which shot
        }
    }

    private void Report(StageContext context, ActivityKind kind, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>Reports at once on the caller's thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
