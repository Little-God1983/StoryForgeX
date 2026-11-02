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
    private static readonly string[] Placeholders = ["{shot.visual}", "{shot.prompt}", "{shot.motion}"];

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
        // What each shot's workflow gets, so the prompt names only pictures the model sees. The prompts
        // come first: a still this run makes has no file yet, but it will before a later shot is painted.
        var toMake = shots.Where((_, i) => todo[i]).Select(s => s.Key).ToHashSet();
        var asked = Enumerable.Range(0, shots.Count)
            .Where(i => todo[i] && queued.GetValueOrDefault(shots[i].Key) is null)
            .Select(i => Describe(
                plan,
                shots[i],
                Sources(plan, shots[i], painter.TakesPictures, painter.TakesMany, fromBefore[i] ? shots[i - 1].Key : null, key => toMake.Contains(key) ? ToBeMade : stored.GetValueOrDefault(key)?.File),
                fromBefore[i]))
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
            var sources = Sources(plan, shot, painter.TakesPictures, painter.TakesMany, before?.ShotKey, key => (made.GetValueOrDefault(key) ?? stored.GetValueOrDefault(key))?.File);
            var yours = queued.GetValueOrDefault(shot.Key);
            // Everyone's look, pictured or not: one the workflow gets no picture of is in words only.
            var prompt = yours ?? Compose(painter.Profile.PromptTemplate, prompts[label], sources.Everyone.Select(p => p.Look));
            var still = await PaintAsync(context, painter, plan, shot, prompt, sources, before, yours is not null, cancellationToken);
            made[shot.Key] = still;
            var result = new CellResult(
                shot.Key, StoredJson.Write(still), Still.SchemaVersion, Hash(context.Project.Setup, shot, sources.Sent, plan.Heard.GetValueOrDefault(shot.Key, ""), sources.Everyone, before, yours, sources.Earlier));
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
        var stored = await cells.CurrentStillsAsync(context.Project.Id, cancellationToken);
        if (painter.TakesPictures && shot.ContinuesPrevious && index > 0)
        {
            var previous = plan.Shots[index - 1];
            var still = stored.GetValueOrDefault(previous.Key);
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
        var sources = Sources(plan, shot, painter.TakesPictures, painter.TakesMany, before?.ShotKey, key => stored.GetValueOrDefault(key)?.File);
        if (painter.TakesPictures)
        {
            foreach (var missing in (shot.EarlierStills ?? []).Where(p => !stored.ContainsKey(p.ShotKey) && plan.Labels.ContainsKey(p.ShotKey)))
            {
                Report(context, ActivityKind.Check, $"{label} picked the still of {plan.Labels[missing.ShotKey]} for {missing.Cast}, which has no still yet: made from the look");
            }
        }
        // Waiting in the batch with your prompt: made with it, as the batch would.
        var yours = (await cells.QueuedStillsAsync(context.Project.Id, cancellationToken)).GetValueOrDefault(key);
        var prompt = yours ?? Compose(
            painter.Profile.PromptTemplate,
            (await PromptsAsync(context, [Describe(plan, shot, sources, before is not null)], cancellationToken))[label],
            sources.Everyone.Select(p => p.Look));
        var made = await PaintAsync(context, painter, plan, shot, prompt, sources, before, yours is not null, cancellationToken);
        return new CellResult(
            key, StoredJson.Write(made), Still.SchemaVersion, Hash(context.Project.Setup, shot, sources.Sent, plan.Heard.GetValueOrDefault(key, ""), sources.Everyone, before, yours, sources.Earlier));
    }

    /// <summary>Everything a still is made from; the same inputs give the same hash (#11 compares them).</summary>
    /// <param name="pictures">The pictures the workflow got.</param>
    /// <param name="heard">What is heard during the shot: Claude writes the prompt from it.</param>
    /// <param name="everyone">Everyone in the shot: their looks go into the prompt.</param>
    /// <param name="earlier">The earlier stills the workflow got.</param>
    internal static string Hash(
        ProjectSetup setup, Shot shot, IReadOnlyList<ShotPicture> pictures, string heard, IReadOnlyList<ShotPicture> everyone, Still? before, string? yourPrompt, IReadOnlyList<EarlierPicture>? earlier = null) => InputHash.Of(new
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
        earlier = (earlier ?? []).Select(e => new { e.ShotKey, e.File }),
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

    /// <summary>A reference image a still is made from: "&lt;image2&gt; shows Karlach".</summary>
    /// <param name="Key">The image's cell, "R01/1".</param>
    /// <param name="File">The picture; null for a reference whose image is gone (in words only).</param>
    internal sealed record ShotPicture(string Key, int Version, string Name, string Look, string? File);

    /// <summary>An earlier still that goes into a shot, for whoever in it was picked from it.</summary>
    /// <param name="Cast">Who it is there for.</param>
    /// <param name="File">Empty when this run makes it first.</param>
    /// <param name="KeepSurroundings">Only places are picked from it: they bring their surroundings along.</param>
    internal sealed record EarlierPicture(IReadOnlyList<string> Cast, string ShotKey, string File, bool KeepSurroundings);

    /// <summary>What goes into one still.</summary>
    /// <param name="Everyone">Everyone in the shot with their look; a picture for those who have one.</param>
    /// <param name="Sent">The reference pictures the workflow gets.</param>
    /// <param name="Earlier">The earlier stills the workflow gets.</param>
    /// <param name="Described">Those of everyone that no picture carries: in words only.</param>
    /// <param name="InOrder">Both kinds of picture, in the order the workflow gets them.</param>
    private sealed record ShotSources(
        IReadOnlyList<ShotPicture> Everyone, IReadOnlyList<ShotPicture> Sent, IReadOnlyList<EarlierPicture> Earlier, IReadOnlyList<ShotPicture> Described, IReadOnlyList<GoingIn> InOrder);

    /// <summary>A picture going into a still, and what the prompt writer hears about it.</summary>
    private sealed record GoingIn(string File, StillPicture Picture);

    /// <summary>The file of a still this run is about to make.</summary>
    private const string ToBeMade = "";

    /// <summary>What every still of a run is made from: the shots, their labels and words, the references.</summary>
    private sealed record Plan(
        IReadOnlyList<Shot> Shots,
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyDictionary<string, string> Heard,
        IReadOnlyList<Reference> References,
        IReadOnlyList<(ReferenceImage Image, int Version)> Images,
        bool TextOnly,
        string Root,
        IReadOnlyList<CastMember> Cast);

    private Task<Plan> PlanAsync(StageContext context, CancellationToken cancellationToken) => PlanAsync(cells, folders, context.Project, cancellationToken);

    /// <summary>
    /// The input hash each still made by the stage would have if it were made now, by shot key: #11
    /// compares it with the one stored. Your own file is left out (nothing made it), and so is a
    /// shot that is gone; with the references not approved, there is nothing to compare with.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> CurrentHashesAsync(
        CellReader cells, ProjectFolders folders, ProfileStore profiles, Project project, IReadOnlyDictionary<string, Still> stills, CancellationToken cancellationToken)
    {
        if (stills.Count == 0)
        {
            return new Dictionary<string, string>();   // nothing to compare: no plan, no profile read
        }
        Plan plan;
        try
        {
            // While the references wait for review, the stills are still compared with the approved images.
            plan = await PlanAsync(cells, folders, project, cancellationToken, requireApproved: false);
        }
        catch (StageFailedException)
        {
            return new Dictionary<string, string>();
        }
        var setup = project.Setup;
        var inputs = (await profiles.GetVersionAsync(setup.Stills.Profile.ProfileId, setup.Stills.Profile.Version, cancellationToken)).Content.Inputs;
        var takesMany = WorkflowTemplate.Maps(inputs, ReferencesStage.ImagesKey);
        var takesPictures = takesMany || WorkflowTemplate.Maps(inputs, ReferencesStage.ImageKey);
        var hashes = new Dictionary<string, string>();
        foreach (var shot in plan.Shots)
        {
            if (stills.GetValueOrDefault(shot.Key) is not { YourFile: false } still)
            {
                continue;
            }
            var before = still.ContinuesFrom is { } from ? stills.GetValueOrDefault(from) : null;
            var sources = Sources(plan, shot, takesPictures, takesMany, still.ContinuesFrom, key => stills.GetValueOrDefault(key)?.File);
            hashes[shot.Key] = Hash(
                setup, shot, sources.Sent, plan.Heard.GetValueOrDefault(shot.Key, ""), sources.Everyone, before, still.YourPrompt ? still.Prompt : null, sources.Earlier);
        }
        return hashes;
    }

    private static async Task<Plan> PlanAsync(CellReader cells, ProjectFolders folders, Project project, CancellationToken cancellationToken, bool requireApproved = true)
    {
        if (requireApproved && await cells.StateAsync(project.Id, PipelineStage.References, cancellationToken) != StageState.Approved)
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
            await folders.RootAsync(cancellationToken),
            board?.Cast ?? []);
    }

    /// <summary>
    /// Who and what is in the shot, in the order the shot names them, with their looks and pictures:
    /// each reference's first image that was made, as approved. Someone with no reference (and
    /// everyone, in a project with no references) has no picture, and neither has a reference whose
    /// image file is gone: they go in by their look.
    /// </summary>
    private static IReadOnlyList<ShotPicture> Pictures(Plan plan, Shot shot)
    {
        var pictures = new List<ShotPicture>();
        foreach (var name in shot.Cast)
        {
            var reference = plan.References.FirstOrDefault(r => ReferenceList.IsNamed(r, name));
            if (reference is null)
            {
                pictures.Add(new ShotPicture("", 0, name, LookOf(plan, name), null));
                continue;
            }
            if (pictures.Any(p => p.Key == reference.Id || p.Key.StartsWith(reference.Id + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            var image = plan.Images
                .Where(i => i.Image.ReferenceId == reference.Id && i.Image.File is not null && i.Image.Made != ImageMaking.NoPicture)
                .OrderBy(i => i.Image.Number)
                .FirstOrDefault(i => File.Exists(Path.Combine(plan.Root, i.Image.File!)));
            var look = reference.Look.Length > 0 ? reference.Look : LookOf(plan, name);
            pictures.Add(image.Image is { } found
                ? new ShotPicture(ReferenceImage.KeyOf(reference.Id, found.Number), image.Version, reference.Name, look, found.File!)
                : new ShotPicture(reference.Id, 0, reference.Name, look, null));
        }
        return pictures;
    }

    /// <summary>The look the shot writer gave someone; empty when none.</summary>
    private static string LookOf(Plan plan, string name) => plan.Cast.FirstOrDefault(c => ReferenceList.Same(c.Name, name))?.Look ?? "";

    /// <summary>Someone's look: their reference's, which you can edit there, else the shot writer's.</summary>
    internal static string LookOf(IReadOnlyList<CastMember> cast, IReadOnlyList<Reference> references, string name) =>
        references.FirstOrDefault(r => ReferenceList.IsNamed(r, name))?.Look is { Length: > 0 } look
            ? look
            : cast.FirstOrDefault(c => ReferenceList.Same(c.Name, name))?.Look ?? "";

    /// <summary>
    /// What goes into a still. Someone seen again with no picture of their own goes in by the earlier
    /// still picked for them, when that shot still comes earlier and has a still; a reference picture
    /// stays the source for whoever has one. A workflow that takes one picture gets one: the still
    /// before, else the first reference, else the first pick that can be used.
    /// </summary>
    /// <param name="beforeKey">The shot a continuation starts from; its still goes in first, outside these.</param>
    /// <param name="fileOf">The file of a shot's still, <see cref="ToBeMade"/> when this run makes it first, null when it has none.</param>
    private static ShotSources Sources(Plan plan, Shot shot, bool takesPictures, bool takesMany, string? beforeKey, Func<string, string?> fileOf)
    {
        var everyone = Pictures(plan, shot);
        var sent = Sent(takesMany, everyone, beforeKey is not null);
        var earlier = new List<EarlierPicture>();
        var carried = new List<string>();   // who an earlier still going in is for
        if (takesPictures)
        {
            var before = plan.Shots.TakeWhile(s => s.Key != shot.Key).Select(s => s.Key).ToHashSet();
            foreach (var pick in shot.EarlierStills ?? [])
            {
                var reference = plan.References.FirstOrDefault(r => ReferenceList.IsNamed(r, pick.Cast));
                var hasPicture = reference is not null && everyone.Any(p => p.File is not null && p.Key.StartsWith(reference.Id + "/", StringComparison.Ordinal));
                var stillShown = plan.Shots.FirstOrDefault(s => s.Key == pick.ShotKey)?.Cast
                    .Any(n => ReferenceList.Same(n, pick.Cast) || (reference is not null && ReferenceList.IsNamed(reference, n))) == true;
                if (hasPicture || !stillShown || !before.Contains(pick.ShotKey) || fileOf(pick.ShotKey) is not { } file
                    || (file != ToBeMade && !File.Exists(Path.Combine(plan.Root, file))))
                {
                    continue;
                }
                if (pick.ShotKey == beforeKey)
                {
                    continue;   // goes in already; their look stays in words, for what a picture does not carry
                }
                var place = plan.Cast.FirstOrDefault(c => ReferenceList.Same(c.Name, pick.Cast))?.Kind == CastKind.Place;
                var index = earlier.FindIndex(e => e.ShotKey == pick.ShotKey);
                if (index >= 0)
                {
                    earlier[index] = earlier[index] with { Cast = [.. earlier[index].Cast, pick.Cast], KeepSurroundings = earlier[index].KeepSurroundings && place };
                }
                else if (takesMany || (beforeKey is null && sent.Count == 0 && earlier.Count == 0))
                {
                    earlier.Add(new EarlierPicture([pick.Cast], pick.ShotKey, file, place));
                }
                else
                {
                    continue;
                }
                carried.Add(pick.Cast);
            }
        }
        var described = everyone.Where(p => !sent.Contains(p) && !carried.Any(name => Names(plan, p, name))).ToList();
        var inOrder = sent.Select(p => (Rank: Rank(plan, shot, [p.Name]), Picture: new GoingIn(p.File!, new StillPicture(p.Name, p.Look))))
            .Concat(earlier.Select(e => (Rank: Rank(plan, shot, e.Cast), Picture: new GoingIn(e.File, new StillPicture(
                string.Join(" and ", e.Cast),
                string.Join(" ", e.Cast.Select(c => everyone.FirstOrDefault(p => Names(plan, p, c))?.Look ?? "").Where(l => l.Length > 0)),
                StillPromptCheck.Name(plan.Labels[e.ShotKey]),
                e.KeepSurroundings)))))
            .OrderBy(p => p.Rank)
            .Select(p => p.Picture)
            .ToList();
        return new ShotSources(everyone, sent, earlier, described, inOrder);
    }

    /// <summary>
    /// Where a picture goes among the others. Qwen edits the first picture and takes content from the
    /// rest, so the place goes first; with no place, whoever the shot names first.
    /// </summary>
    private static int Rank(Plan plan, Shot shot, IReadOnlyList<string> names)
    {
        var place = names.Any(name => plan.Cast.Any(c => c.Kind == CastKind.Place && ReferenceList.Same(c.Name, name))
            || plan.References.Any(r => r.Kind == CastKind.Place && ReferenceList.IsNamed(r, name)));
        if (place)
        {
            return -1;
        }
        var named = names.SelectMany(name => shot.Cast.Select((c, i) => (c, i))
            .Where(x => ReferenceList.Same(x.c, name) || plan.References.Any(r => ReferenceList.IsNamed(r, x.c) && ReferenceList.IsNamed(r, name)))
            .Select(x => x.i));
        return named.DefaultIfEmpty(int.MaxValue).Min();
    }

    /// <summary>Whether this name is who the picture is of; a reference may go by several names.</summary>
    private static bool Names(Plan plan, ShotPicture picture, string name) =>
        ReferenceList.Same(picture.Name, name) || plan.References.Any(r => r.Name == picture.Name && ReferenceList.IsNamed(r, name));

    /// <summary>
    /// The reference pictures the workflow gets: all of them when it takes a list ('images'); with one
    /// picture only ('image'), the still before for a continuation, else the first reference.
    /// </summary>
    private static IReadOnlyList<ShotPicture> Sent(bool takesMany, IReadOnlyList<ShotPicture> pictures, bool fromBefore)
    {
        var pictured = pictures.Where(p => p.File is not null).ToList();
        return takesMany ? pictured : fromBefore ? [] : [.. pictured.Take(1)];
    }

    private static StillShot Describe(Plan plan, Shot shot, ShotSources sources, bool fromBefore)
    {
        var pictures = sources.InOrder.Select(p => p.Picture).ToList();
        var described = sources.Described.Select(p => new StillPicture(p.Name, p.Look)).ToList();
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
        StageContext context, Painter painter, Plan plan, Shot shot, string prompt, ShotSources sources, Still? before, bool yours,
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
            files.AddRange(sources.InOrder.Select(p => p.File));
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
            return new Still(
                shot.Key, stored, prompt, seed, size, painter.Profile.WorkflowTemplate, [.. sources.Sent.Select(p => p.Key)], before?.ShotKey, yours,
                EarlierStills: sources.Earlier.Count == 0 ? null : [.. sources.Earlier.SelectMany(e => e.Cast.Select(c => new EarlierStill(c, e.ShotKey)))]);
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
