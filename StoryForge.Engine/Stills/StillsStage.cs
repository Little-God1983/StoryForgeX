using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.References;
using StoryForge.Engine.Research;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Storyboard;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.Stills;

/// <summary>The Stills stage's whole result: which shots got a still.</summary>
internal sealed record StillsSheet(IReadOnlyList<string> ShotKeys);

/// <summary>
/// The Stills stage: one still per shot, made through the still profile's ComfyUI workflow from
/// the approved reference images of who and what is in the shot (none with "text-to-image only").
/// Claude writes each shot's prompt from the storyboard and the profile's instructions; the
/// profile's template wraps it. A still is never made from the shot before, except for a shot the
/// storyboard marks as continuing it: that one starts from the still before, one step at most (a
/// continuation of a continuation starts from the references again). Each still is a cell of its
/// own, keyed by its shot.
/// A run makes what is waiting: every shot with no still yet, and every still in the batch
/// (Regenerate, Edit prompt…), in shot order, with one prompt call for all of them. The first run
/// makes them all; Continue pipeline makes the batch; Retry after a failure makes the ones missing.
/// </summary>
internal sealed partial class StillsStage(
    IComfyUi comfy,
    IStillPromptAgent agent,
    CellReader cells,
    ProfileStore profiles,
    SettingsStore settings,
    ProjectFolders folders,
    TimeProvider clock) : ISegmentWorker
{
    /// <summary>The profile input keys the stage fills besides the ones a reference image takes; unmapped keys keep the workflow's own value.</summary>
    public const string NegativeKey = "negative";
    public const string StepsKey = "steps";

    /// <summary>The first answer and two corrections.</summary>
    public const int Tries = 3;

    /// <summary>Where the shot's prompt goes in the profile's template; without one it goes first.</summary>
    private static readonly string[] Placeholders = ["{shot.visual}", "{shot.prompt}"];

    /// <summary>Where the looks of who and what is in the shot go.</summary>
    private const string LooksPlaceholder = "{characters.sheet}";

    public PipelineStage Stage => PipelineStage.Stills;

    public string CancelledAfter(int stored) =>
        $"Cancelled after {stored} {(stored == 1 ? "still" : "stills")}. Those are kept; Continue pipeline makes the ones still waiting.";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        // Before Claude is asked: a profile that cannot make the stills fails at once.
        var painter = await PainterAsync(context, cancellationToken);
        var shots = plan.Shots;
        var stored = await cells.CurrentStillsAsync(context.Project.Id, cancellationToken);
        var queued = await cells.QueuedStillsAsync(context.Project.Id, cancellationToken);
        var todo = shots.Select(s => !stored.ContainsKey(s.Key) || queued.ContainsKey(s.Key)).ToArray();

        // A continuation starts from the still before, unless that one started from its own shot
        // before; a text-to-image workflow takes no picture to start from.
        var fromBefore = new bool[shots.Count];
        for (var i = 1; i < shots.Count; i++)
        {
            var previous = stored.GetValueOrDefault(shots[i - 1].Key);
            fromBefore[i] = painter.TakesPictures && shots[i].ContinuesPrevious && (todo[i - 1]
                ? !fromBefore[i - 1]
                : previous is { ContinuesFrom: null } && File.Exists(Path.Combine(painter.Root, previous.File)));
        }
        // What each shot's workflow gets, so the prompt names only pictures the model sees.
        var all = shots.Select(s => Pictures(plan, s)).ToList();
        var sent = shots.Select((s, i) => Sent(painter, all[i], fromBefore[i])).ToList();
        var asked = Enumerable.Range(0, shots.Count)
            .Where(i => todo[i] && queued.GetValueOrDefault(shots[i].Key) is null)
            .Select(i => Describe(plan, shots[i], sent[i], all[i], fromBefore[i]))
            .ToList();
        var prompts = asked.Count > 0 ? await PromptsAsync(context, asked, cancellationToken) : new Dictionary<string, string>();

        var results = new List<CellResult>();
        var made = new Dictionary<string, Still>();
        var count = todo.Count(t => t);
        for (var i = 0; i < shots.Count; i++)
        {
            if (!todo[i])
            {
                continue;
            }
            var shot = shots[i];
            var label = plan.Labels[shot.Key];
            Report(context, ActivityKind.Model, $"{label} ({results.Count + 1} of {count})");
            var before = fromBefore[i] ? made.GetValueOrDefault(shots[i - 1].Key) ?? stored[shots[i - 1].Key] : null;
            var pictures = sent[i];
            var yours = queued.GetValueOrDefault(shot.Key);
            // Everyone's look, pictured or not: one the workflow gets no picture of is in words only.
            var prompt = yours ?? Compose(painter.Profile.PromptTemplate, prompts[label], all[i].Select(p => p.Look));
            var still = await PaintAsync(context, painter, plan, shot, prompt, pictures, before, yours is not null, cancellationToken);
            made[shot.Key] = still;
            var result = new CellResult(
                shot.Key, StoredJson.Write(still), Still.SchemaVersion, Hash(context.Project.Setup, shot, pictures, plan.Heard.GetValueOrDefault(shot.Key, ""), all[i], before, yours));
            // Stored at once: a still takes a while, and you look at it while the next is made.
            if (context.StoreSegment is { } store)
            {
                await store(result);
            }
            results.Add(result);
        }
        Report(context, ActivityKind.Check, results.Count == 0 ? "every shot has its still: nothing was waiting" : $"{results.Count} {(results.Count == 1 ? "still" : "stills")} made");
        // No segments: the stills not made in this run stay as they are, and every one made is stored already.
        return new StageResult(
            StoredJson.Write(new StillsSheet([.. made.Keys])),
            Still.SchemaVersion,
            InputHash.Of(results.Select(r => r.InputHash)));
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var painter = await PainterAsync(context, cancellationToken);
        var index = plan.Shots.ToList().FindIndex(s => s.Key == key);
        if (index < 0)
        {
            throw new StageFailedException($"The storyboard has no shot {key}.");
        }
        var shot = plan.Shots[index];
        var label = plan.Labels[key];
        Still? before = null;
        if (painter.TakesPictures && shot.ContinuesPrevious && index > 0)
        {
            var previous = plan.Shots[index - 1];
            var still = (await cells.CurrentStillsAsync(context.Project.Id, cancellationToken)).GetValueOrDefault(previous.Key);
            if (still is null || !File.Exists(Path.Combine(painter.Root, still.File)))
            {
                Report(context, ActivityKind.Check, $"{label} continues {plan.Labels[previous.Key]}, which has no still yet: made from the references");
            }
            else if (still.ContinuesFrom is not null)
            {
                Report(context, ActivityKind.Check, $"{label} continues {plan.Labels[previous.Key]}, which continues the one before it: made from the references (one step at most)");
            }
            else
            {
                before = still;
            }
        }
        var everyone = Pictures(plan, shot);
        var pictures = Sent(painter, everyone, before is not null);
        // Waiting in the batch with your prompt: made with it, as the batch would.
        var yours = (await cells.QueuedStillsAsync(context.Project.Id, cancellationToken)).GetValueOrDefault(key);
        var prompt = yours ?? Compose(
            painter.Profile.PromptTemplate,
            (await PromptsAsync(context, [Describe(plan, shot, pictures, everyone, before is not null)], cancellationToken))[label],
            everyone.Select(p => p.Look));
        var made = await PaintAsync(context, painter, plan, shot, prompt, pictures, before, yours is not null, cancellationToken);
        return new CellResult(
            key, StoredJson.Write(made), Still.SchemaVersion, Hash(context.Project.Setup, shot, pictures, plan.Heard.GetValueOrDefault(key, ""), everyone, before, yours));
    }

    /// <summary>Everything a still is made from; the same inputs give the same hash (#11 compares them).</summary>
    /// <param name="pictures">The pictures the workflow got.</param>
    /// <param name="heard">What is heard during the shot: Claude writes the prompt from it.</param>
    /// <param name="everyone">Everyone in the shot with a reference: their looks go into the prompt.</param>
    internal static string Hash(
        ProjectSetup setup, Shot shot, IReadOnlyList<ShotPicture> pictures, string heard, IReadOnlyList<ShotPicture> everyone, Still? before, string? yourPrompt) => InputHash.Of(new
    {
        stage = PipelineStage.Stills,
        schema = Still.SchemaVersion,
        shot.Description,
        shot.Cast,
        shot.ContinuesPrevious,
        pictures = pictures.Select(p => new { p.Key, p.Version }),
        heard,
        looks = everyone.Select(p => new { p.Name, p.Look }),
        before = before?.File,
        yourPrompt,
        consistency = setup.References.Consistency,
        writer = new { setup.Script.Provider, setup.Script.Model },
        provider = setup.Stills.Provider,
        model = setup.Stills.Model,
        profile = setup.Stills.Profile,
        size = setup.Stills.Size,
    });

    /// <summary>
    /// The profile's template with the shot's prompt in its placeholder, and the looks of who is in
    /// it where it asks for them; without a placeholder, the prompt goes first and the template after.
    /// </summary>
    internal static string Compose(string template, string prompt, IEnumerable<string> looks)
    {
        var text = template.Trim();
        if (text.Length == 0)
        {
            return prompt;
        }
        var placeholder = Placeholders.FirstOrDefault(p => text.Contains(p, StringComparison.Ordinal));
        text = placeholder is null ? $"{prompt}, {text}" : text.Replace(placeholder, prompt, StringComparison.Ordinal);
        text = text.Replace(LooksPlaceholder, string.Join(" ", looks.Where(l => l.Length > 0)), StringComparison.Ordinal);
        // An empty placeholder leaves ", ," behind.
        return EmptyParts().Replace(text, ", ").Trim().Trim(',').Trim();
    }

    [GeneratedRegex(@"\s*,(\s*,)+\s*")]
    private static partial Regex EmptyParts();

    /// <summary>A reference image a still is made from: "picture 2 shows Karlach".</summary>
    /// <param name="Key">The image's cell, "R01/1".</param>
    /// <param name="File">The picture; null for a reference whose image is gone (in words only).</param>
    internal sealed record ShotPicture(string Key, int Version, string Name, string Look, string? File);

    /// <summary>What every still of a run is made from: the shots, their labels and words, the references.</summary>
    private sealed record Plan(
        IReadOnlyList<Shot> Shots,
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyDictionary<string, string> Heard,
        IReadOnlyList<Reference> References,
        IReadOnlyList<(ReferenceImage Image, int Version)> Images,
        bool TextOnly,
        string Root);

    private async Task<Plan> PlanAsync(StageContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        if (await cells.StateAsync(project.Id, PipelineStage.References, cancellationToken) != StageState.Approved)
        {
            throw new StageFailedException("Approve the references first: the stills are made from them.");
        }
        var shots = await cells.CurrentShotsAsync(project.Id, cancellationToken);
        if (shots.Count == 0)
        {
            throw new StageFailedException("The storyboard has no shots yet.");
        }
        var textOnly = project.Setup.References.Consistency == Consistency.TextToImageOnly;
        var references = textOnly ? [] : (await cells.CurrentReferencesAsync(project.Id, cancellationToken))?.References ?? [];
        var images = textOnly ? [] : await cells.ApprovedReferenceImagesAsync(project.Id, cancellationToken);

        // What is heard during each shot, from the voice the storyboard was cut from.
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
        return new Plan(
            shots,
            shots.ToDictionary(s => s.Key, s => ShotTiming.Label(shots, s.Key)),
            heard,
            references,
            images,
            textOnly,
            await folders.RootAsync(cancellationToken));
    }

    /// <summary>
    /// The pictures of who and what is in the shot: each reference's first image that was made, as
    /// approved, in the order the shot names them. Someone with no reference is in the prompt only;
    /// a reference whose image file is gone comes with no picture, to go in by its look.
    /// </summary>
    private static IReadOnlyList<ShotPicture> Pictures(Plan plan, Shot shot)
    {
        if (plan.TextOnly)
        {
            return [];
        }
        var pictures = new List<ShotPicture>();
        foreach (var name in shot.Cast)
        {
            var reference = plan.References.FirstOrDefault(r => ReferenceList.IsNamed(r, name));
            if (reference is null || pictures.Any(p => p.Key == reference.Id || p.Key.StartsWith(reference.Id + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            var image = plan.Images
                .Where(i => i.Image.ReferenceId == reference.Id && i.Image.File is not null && i.Image.Made != ImageMaking.NoPicture)
                .OrderBy(i => i.Image.Number)
                .FirstOrDefault(i => File.Exists(Path.Combine(plan.Root, i.Image.File!)));
            pictures.Add(image.Image is { } found
                ? new ShotPicture(ReferenceImage.KeyOf(reference.Id, found.Number), image.Version, reference.Name, reference.Look, found.File!)
                : new ShotPicture(reference.Id, 0, reference.Name, reference.Look, null));
        }
        return pictures;
    }

    /// <summary>
    /// The reference pictures the workflow gets: all of them when it takes a list ('images'); with one
    /// picture only ('image'), the still before for a continuation, else the first reference.
    /// </summary>
    private static IReadOnlyList<ShotPicture> Sent(Painter painter, IReadOnlyList<ShotPicture> pictures, bool fromBefore)
    {
        var pictured = pictures.Where(p => p.File is not null).ToList();
        return painter.TakesMany ? pictured : fromBefore ? [] : [.. pictured.Take(1)];
    }

    /// <param name="everyone">The pictures of everyone in the shot with a reference; those not sent are described in words.</param>
    private static StillShot Describe(Plan plan, Shot shot, IReadOnlyList<ShotPicture> sent, IReadOnlyList<ShotPicture> everyone, bool fromBefore)
    {
        var pictures = sent.Select(p => new StillPicture(p.Name, p.Look)).ToList();
        var described = everyone.Where(p => !sent.Contains(p)).Select(p => new StillPicture(p.Name, p.Look)).ToList();
        if (fromBefore)
        {
            pictures.Insert(0, new StillPicture("the shot before", ""));
        }
        return new StillShot(plan.Labels[shot.Key], shot.Description, shot.Cast, plan.Heard.GetValueOrDefault(shot.Key, ""), pictures, fromBefore, described);
    }

    /// <summary>Claude's prompt for each shot, by label; a failed check goes back to it, twice.</summary>
    private async Task<IReadOnlyDictionary<string, string>> PromptsAsync(StageContext context, IReadOnlyList<StillShot> shots, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (setup.Script.Provider != ResearchStage.ClaudeCli)
        {
            throw new StageFailedException(
                $"The still prompts are written through Claude CLI for now, and this project uses {setup.Script.Provider}. LM Studio follows with #19.");
        }
        var profile = await profiles.GetVersionAsync(setup.Stills.Profile.ProfileId, setup.Stills.Profile.Version, cancellationToken);
        var request = new StillPromptRequest(context.Project.Id, setup.Brief, shots, profile.Content.Instructions, setup.Script.Model);
        string? session = null;
        IReadOnlyList<string> problems = [];
        var tries = 0;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            tries = attempt;
            var answer = await agent.WriteAsync(request, session, problems, context.Activity, cancellationToken);
            session = answer.Session;
            problems = StillPromptCheck.Problems(answer.Output, shots);
            if (problems.Count == 0)
            {
                return StillPromptCheck.ByLabel(answer.Output!, shots);
            }
            var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
            var last = attempt == Tries || session is null;
            Report(context, ActivityKind.Check, $"try {attempt}: {problems[0]}{more} – {(last ? "stage failed" : "sent back")}");
            if (session is null)
            {
                break;
            }
        }
        throw new StageFailedException($"The answer had no good prompts after {tries} {(tries == 1 ? "try" : "tries")}. Last problem: {problems[0]}");
    }

    /// <summary>What every still is made with: the still profile's workflow, and whether it takes pictures.</summary>
    /// <param name="TakesMany">It takes a list of pictures ('images'), not only one ('image').</param>
    /// <param name="Uploaded">The files uploaded to ComfyUI in this run, with the name it gave each: every one goes once.</param>
    private sealed record Painter(JsonObject Workflow, ProfileContent Profile, bool TakesPictures, bool TakesMany, string Root, string Folder, Dictionary<string, string> Uploaded);

    private async Task<Painter> PainterAsync(StageContext context, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (!string.Equals(setup.Stills.Provider, VoiceStage.ComfyUi, StringComparison.OrdinalIgnoreCase))
        {
            throw new StageFailedException($"Stills are made through ComfyUI for now, and this project uses {setup.Stills.Provider}.");
        }
        var profile = (await profiles.GetVersionAsync(setup.Stills.Profile.ProfileId, setup.Stills.Profile.Version, cancellationToken)).Content;
        var templates = (await settings.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
        var workflow = await WorkflowTemplate.LoadAsync(templates, profile.WorkflowTemplate, cancellationToken, "image");
        if (!WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.PromptKey))
        {
            throw new StageFailedException($"The still profile does not say where the prompt goes. Map '{ReferencesStage.PromptKey}' to the prompt node's text.");
        }
        var takesPictures = WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.ImageKey) || WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.ImagesKey);
        if (!takesPictures && setup.References.Consistency == Consistency.ReferenceImages)
        {
            throw new StageFailedException(
                $"The still profile does not say where the reference pictures go. Map '{ReferencesStage.ImageKey}' (or '{ReferencesStage.ImagesKey}') to the node that loads them.");
        }
        return new Painter(
            workflow, profile, takesPictures, WorkflowTemplate.Maps(profile.Inputs, ReferencesStage.ImagesKey), await folders.RootAsync(cancellationToken), ProjectFolders.Of(context.Project), []);
    }

    /// <summary>One ComfyUI run; the still is saved as stills/&lt;shot&gt;-&lt;take&gt;.png in the project folder.</summary>
    /// <param name="before">The still of the shot before, for a continuation: the first picture.</param>
    private async Task<Still> PaintAsync(
        StageContext context, Painter painter, Plan plan, Shot shot, string prompt, IReadOnlyList<ShotPicture> pictures, Still? before, bool yours,
        CancellationToken cancellationToken)
    {
        var label = plan.Labels[shot.Key];
        var size = context.Project.Setup.Stills.Size;
        var seed = Random.Shared.Next();
        try
        {
            var files = new List<string>();
            if (before is not null)
            {
                files.Add(before.File);
            }
            files.AddRange(pictures.Select(p => p.File!));
            if (files.Count == 0 && painter.TakesPictures)
            {
                // Nothing to start from: an edit workflow paints on a blank canvas, not on the picture it was saved with.
                files.Add(await CanvasAsync(painter, size, cancellationToken));
            }
            var uploaded = new List<string>();
            foreach (var file in files)
            {
                if (!painter.Uploaded.TryGetValue(file, out var name))
                {
                    painter.Uploaded[file] = name = await comfy.UploadAsync(Path.Combine(painter.Root, file), cancellationToken);
                }
                uploaded.Add(name);
            }
            var values = new Dictionary<string, JsonNode>
            {
                [ReferencesStage.PromptKey] = prompt,
                [ReferencesStage.SeedKey] = seed,
                [ReferencesStage.WidthKey] = size.Width,
                [ReferencesStage.HeightKey] = size.Height,
            };
            if (painter.Profile.NegativePrompt.Trim() is { Length: > 0 } negative)
            {
                values[NegativeKey] = negative;
            }
            if (painter.Profile.Steps is { } steps)
            {
                values[StepsKey] = steps;
            }
            if (uploaded.Count > 0)
            {
                values[ReferencesStage.ImageKey] = uploaded[0];
                values[ReferencesStage.ImagesKey] = JsonSerializer.Serialize(uploaded.Select(name => new { name, subfolder = "", type = "input", enabled = true }));
            }
            var made = await comfy.RunAsync(
                WorkflowTemplate.Apply(painter.Workflow, painter.Profile.Inputs, values),
                $"StoryForge · {context.Project.Setup.Name} · still {label}",
                new Progress(line => Report(context, ActivityKind.Model, $"{label}: {line}")),
                cancellationToken);
            var picture = made.FirstOrDefault(f => f.Kind == "images" && Path.GetExtension(f.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                ?? throw new StageFailedException("The workflow saved no image. It needs a node that saves it, e.g. Save Image.");
            var stored = Path.Combine(painter.Folder, "stills", $"{FileName(shot.Key)}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(picture.Name)}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(painter.Root, stored))!);
            await File.WriteAllBytesAsync(Path.Combine(painter.Root, stored), picture.Content, cancellationToken);
            Report(context, ActivityKind.Check, $"{label} made{(before is null ? "" : $", from the still of {plan.Labels[before.ShotKey]}")}");
            return new Still(shot.Key, stored, prompt, seed, size, painter.Profile.WorkflowTemplate, [.. pictures.Select(p => p.Key)], before?.ShotKey, yours);
        }
        catch (StageFailedException ex)
        {
            throw new StageFailedException($"{label}: {ex.Message}");   // the bar says which shot
        }
    }

    /// <summary>"S01-3f9a": a shot key as part of a file name.</summary>
    internal static string FileName(string key) => key.Replace('/', '-');

    /// <summary>A blank canvas the size of the stills in the project folder, written the first time it is needed.</summary>
    private static async Task<string> CanvasAsync(Painter painter, GenerationSize size, CancellationToken cancellationToken)
    {
        var file = Path.Combine(painter.Folder, "stills", $"blank-{size.Width}x{size.Height}.png");
        var path = Path.Combine(painter.Root, file);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, BlankCanvas.Png(size.Width, size.Height), cancellationToken);
        }
        return file;
    }

    private void Report(StageContext context, ActivityKind kind, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>Reports at once on the caller's thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
