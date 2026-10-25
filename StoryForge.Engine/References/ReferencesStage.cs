using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Profiles;
using StoryForge.Engine.Projects;
using StoryForge.Engine.Settings;
using StoryForge.Engine.Voice;

namespace StoryForge.Engine.References;

/// <summary>
/// The References stage. First the list: who and what the storyboard shows often enough (or once,
/// carrying the story), with an image pool from the project's sources; with the list review on,
/// the run stops there. Then the images: Claude looks at each reference's first good picture,
/// Qwen cleans it up (or fills in what is missing) through the image profile's ComfyUI workflow.
/// With no usable picture nothing is made, and that image cannot be approved. Every image is a
/// cell of its own, keyed "R01/1", and can be made again on its own.
/// </summary>
internal sealed class ReferencesStage(
    IComfyUi comfy,
    IPictureCheck check,
    PoolBuilder pool,
    CellReader cells,
    ReferenceSheets sheets,
    ProfileStore profiles,
    SettingsStore settings,
    ProjectFolders folders,
    TimeProvider clock) : ISegmentWorker
{
    /// <summary>The profile input keys the stage fills; a key the profile does not map keeps the workflow's own value.</summary>
    public const string PromptKey = "prompt";
    public const string ImageKey = "image";
    public const string ImagesKey = "images";
    public const string SeedKey = "seed";
    public const string WidthKey = "width";
    public const string HeightKey = "height";

    /// <summary>Reference images are square, about a megapixel: what Qwen works at.</summary>
    public const int Side = 1024;

    /// <summary>The most source pictures one image is made from.</summary>
    public const int MaxSources = 3;

    public PipelineStage Stage => PipelineStage.References;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        if (project.Setup.Stills.Consistency == Consistency.TextToImageOnly)
        {
            Report(context, ActivityKind.Check, "This project makes its stills from text only: no references are needed.");
            return Result(new ReferenceSheet([], [], [], ReferencePhase.Images), []);
        }

        var sheet = await cells.CurrentReferencesAsync(project.Id, cancellationToken);
        if (sheet is not { Phase: ReferencePhase.Images })
        {
            sheet = await ProposeAsync(context, cancellationToken);
            if (project.Setup.ReviewReferenceList)
            {
                Report(context, ActivityKind.Check, $"{sheet.References.Count} references proposed, {sheet.Pool.Count} pictures in the pool: check the list, then make the images.");
                return Result(sheet, null);
            }
            sheet = sheet with { Phase = ReferencePhase.Images };
        }

        // Every image each reference has (one to start with), made again.
        var existing = await cells.ReferenceImagesAsync(project.Id, cancellationToken);
        Maker? maker = null;   // an empty list makes nothing, so needs no workflow
        var results = new List<CellResult>();
        var references = new List<Reference>();
        foreach (var reference in sheet.References)
        {
            var updated = reference;
            var numbers = existing.Where(i => i.ReferenceId == reference.Id).Select(i => i.Number).DefaultIfEmpty(1).Distinct().Order();
            foreach (var number in numbers)
            {
                maker ??= await MakerAsync(context, cancellationToken);
                (var image, updated) = await MakeAsync(context, maker, sheet, updated, number, cancellationToken);
                var result = new CellResult(ReferenceImage.KeyOf(reference.Id, number), StoredJson.Write(image), ReferenceSheet.SchemaVersion, Hash(project.Setup, updated));
                if (context.StoreSegment is { } store)
                {
                    await store(result);
                }
                results.Add(result);
            }
            references.Add(updated);
        }
        sheet = sheet with { References = references };
        Report(context, ActivityKind.Check, $"{references.Count} references, {results.Count} images");
        return Result(sheet, results);
    }

    public async Task<CellResult> RunSegmentAsync(StageContext context, string key, CancellationToken cancellationToken)
    {
        var sheet = await cells.CurrentReferencesAsync(context.Project.Id, cancellationToken)
            ?? throw new StageFailedException("There are no references yet.");
        var (referenceId, number) = Parse(key);
        var reference = sheet.References.FirstOrDefault(r => r.Id == referenceId) ?? throw new StageFailedException($"There is no reference {referenceId}.");
        var maker = await MakerAsync(context, cancellationToken);
        var (image, updated) = await MakeAsync(context, maker, sheet, reference, number, cancellationToken);
        if (updated.Look != reference.Look)
        {
            // Claude saw how it looks: the sheet keeps it, so its next images are made the same way.
            var now = await cells.CurrentReferencesAsync(context.Project.Id, cancellationToken);
            if (now?.References.FirstOrDefault(r => r.Id == referenceId) is { Look.Length: 0 } still)
            {
                await sheets.WriteAsync(
                    context.Project.Id, now with { References = [.. now.References.Select(r => r == still ? still with { Look = updated.Look } : r)] }, [], cancellationToken);
            }
        }
        return new CellResult(key, StoredJson.Write(image), ReferenceSheet.SchemaVersion, Hash(context.Project.Setup, updated));
    }

    /// <summary>Everything a reference image is made from; the same inputs give the same hash (#11 compares them).</summary>
    internal static string Hash(ProjectSetup setup, Reference reference) => InputHash.Of(new
    {
        stage = PipelineStage.References,
        schema = ReferenceSheet.SchemaVersion,
        reference.Name,
        reference.Kind,
        reference.Look,
        reference.Sources,
        reference.DescriptionOnly,
        provider = setup.Stills.Provider,
        model = setup.Stills.Model,
        profile = setup.Stills.Profile,
    });

    /// <summary>"R01/2" → ("R01", 2).</summary>
    internal static (string ReferenceId, int Number) Parse(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && int.TryParse(key.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (key[..slash], number)
            : throw new StageFailedException($"\"{key}\" names no reference image.");
    }

    private StageResult Result(ReferenceSheet sheet, IReadOnlyList<CellResult>? images) =>
        new(StoredJson.Write(sheet), ReferenceSheet.SchemaVersion, InputHash.Of(new { stage = PipelineStage.References, sheet }), images);

    /// <summary>The list from the storyboard, and the pool: pictures for every name, and on the pages the facts came from.</summary>
    private async Task<ReferenceSheet> ProposeAsync(StageContext context, CancellationToken cancellationToken)
    {
        var project = context.Project;
        if (await cells.StateAsync(project.Id, PipelineStage.Storyboard, cancellationToken) != StageState.Approved)
        {
            throw new StageFailedException("Approve the storyboard first: the references are proposed from it.");
        }
        var board = await cells.CurrentStoryboardAsync(project.Id, cancellationToken)
            ?? throw new StageFailedException("The storyboard has not been planned yet. Approve it, and the references are proposed.");
        var shots = await cells.CurrentShotsAsync(project.Id, cancellationToken);
        var (references, others) = ReferenceList.Propose(board, shots);
        var facts = (await cells.ApprovedFactSheetAsync(project.Id, cancellationToken))?.Sheet.Facts ?? [];
        var factPages = facts.Where(f => !f.LeftOut).Select(f => f.SourceUrl).Distinct().ToList();
        var root = await folders.RootAsync(cancellationToken);
        var (found, images) = await pool.BuildAsync(
            references, [], [], project.Setup.ResearchSources, factPages, root, ProjectFolders.Of(project), cancellationToken,
            new Progress(line => Report(context, ActivityKind.Search, line)));
        return new ReferenceSheet(found, images, others, ReferencePhase.List);
    }

    /// <summary>What every image is made with: the image profile's workflow, and where the images go.</summary>
    private sealed record Maker(JsonObject Workflow, ProfileContent Profile, string Root, string Folder);

    private async Task<Maker> MakerAsync(StageContext context, CancellationToken cancellationToken)
    {
        var setup = context.Project.Setup;
        if (!string.Equals(setup.Stills.Provider, VoiceStage.ComfyUi, StringComparison.OrdinalIgnoreCase))
        {
            throw new StageFailedException($"Reference images are made through ComfyUI for now, and this project uses {setup.Stills.Provider}.");
        }
        var profile = (await profiles.GetVersionAsync(setup.Stills.Profile.ProfileId, setup.Stills.Profile.Version, cancellationToken)).Content;
        var templates = (await settings.LoadAsync(cancellationToken)).ComfyUi.WorkflowTemplatesFolder;
        var workflow = await WorkflowTemplate.LoadAsync(templates, profile.WorkflowTemplate, cancellationToken, "image");
        if (!WorkflowTemplate.Maps(profile.Inputs, PromptKey))
        {
            throw new StageFailedException($"The image profile does not say where the prompt goes. Map '{PromptKey}' to the prompt node's text.");
        }
        return new Maker(workflow, profile, await folders.RootAsync(cancellationToken), ProjectFolders.Of(context.Project));
    }

    /// <summary>
    /// One image of a reference: from its first picture Claude says shows it (filled in where part of
    /// it is missing), from its look alone when you chose that, or nothing when no picture is usable.
    /// Returns the reference with the look Claude saw, when it had none.
    /// </summary>
    private async Task<(ReferenceImage Image, Reference Reference)> MakeAsync(
        StageContext context, Maker maker, ReferenceSheet sheet, Reference reference, int number, CancellationToken cancellationToken)
    {
        var seed = Random.Shared.Next();
        if (reference.DescriptionOnly)
        {
            var canvas = await CanvasAsync(maker, cancellationToken);
            var file = await RenderAsync(context, maker, reference, number, [canvas], Prompt(reference, ImageMaking.FromDescription, ""), seed, cancellationToken);
            return (new ReferenceImage(reference.Id, number, file, [], seed, ImageMaking.FromDescription, ""), reference);
        }

        var pictures = reference.Sources.Select(id => sheet.Pool.FirstOrDefault(p => p.Id == id)).OfType<PoolImage>().ToList();
        var usable = pictures.Where(p => !p.TooSmall).ToList();
        if (usable.Count == 0)
        {
            var why = pictures.Count > 0 ? $"Only pictures under {PoolImage.MinUsableSide} px were found." : "No picture was found on the sources.";
            Report(context, ActivityKind.Check, $"{reference.Name}: {why} Nothing is made until you choose.");
            return (new ReferenceImage(reference.Id, number, null, [], seed, ImageMaking.NoPicture, why), reference);
        }

        var passedOver = new List<PoolImage>();
        foreach (var picture in usable)
        {
            PictureVerdict verdict;
            try
            {
                verdict = await check.CheckAsync(
                    new PictureCheckRequest(context.Project.Id, reference.Name, reference.Kind, reference.KeyReason, Path.Combine(maker.Root, picture.File), context.Project.Setup.Writing.Model),
                    context.Activity, cancellationToken);
            }
            catch (StageFailedException ex)
            {
                throw new StageFailedException($"{reference.Name}: {ex.Message}");
            }
            if (!verdict.Usable)
            {
                Report(context, ActivityKind.Check, $"{reference.Name}: {picture.Id} does not show it, passed over");
                passedOver.Add(picture);
                continue;
            }
            var looked = reference.Look.Length > 0 ? reference : reference with { Look = verdict.Look };
            var made = verdict.Complete ? ImageMaking.FromPicture : ImageMaking.PartlyMadeUp;
            var sources = new[] { picture }.Concat(usable.Where(p => p != picture && !passedOver.Contains(p))).Take(MaxSources).ToList();
            var file = await RenderAsync(context, maker, looked, number, [.. sources.Select(s => s.File)], Prompt(looked, made, verdict.Missing), seed, cancellationToken);
            return (new ReferenceImage(reference.Id, number, file, [.. sources.Select(s => s.Id)], seed, made, made == ImageMaking.PartlyMadeUp ? verdict.Missing : ""), looked);
        }
        Report(context, ActivityKind.Check, $"{reference.Name}: none of the pictures shows it. Nothing is made until you choose.");
        return (new ReferenceImage(reference.Id, number, null, [], seed, ImageMaking.NoPicture, $"None of the pictures found shows {reference.Name}."), reference);
    }

    /// <summary>The blank canvas in the project folder (references/blank.png), written the first time it is needed.</summary>
    private static async Task<string> CanvasAsync(Maker maker, CancellationToken cancellationToken)
    {
        var file = Path.Combine(maker.Folder, "references", "blank.png");
        var path = Path.Combine(maker.Root, file);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, BlankCanvas.Png(Side), cancellationToken);
        }
        return file;
    }

    /// <summary>One ComfyUI run; the image is saved as references/R01-1-&lt;take&gt;.png in the project folder.</summary>
    /// <param name="pictures">The pictures it is made from, as files in the projects folder; the first is the main one.</param>
    private async Task<string> RenderAsync(
        StageContext context, Maker maker, Reference reference, int number, IReadOnlyList<string> pictures, string prompt, int seed, CancellationToken cancellationToken)
    {
        try
        {
            var uploaded = new List<string>();
            foreach (var source in pictures)
            {
                uploaded.Add(await comfy.UploadAsync(Path.Combine(maker.Root, source), cancellationToken));
            }
            var values = new Dictionary<string, JsonNode>
            {
                [PromptKey] = prompt,
                [SeedKey] = seed,
                [WidthKey] = Side,
                [HeightKey] = Side,
            };
            if (uploaded.Count > 0)
            {
                values[ImageKey] = uploaded[0];
                values[ImagesKey] = JsonSerializer.Serialize(uploaded);
            }
            var name = $"{reference.Name} image {number}";
            var files = await comfy.RunAsync(
                WorkflowTemplate.Apply(maker.Workflow, maker.Profile.Inputs, values),
                $"StoryForge · {context.Project.Setup.Name} · {name}",
                new Progress(line => Report(context, ActivityKind.Model, $"{name}: {line}")),
                cancellationToken);
            var picture = files.FirstOrDefault(f => f.Kind == "images" && Path.GetExtension(f.Name) is ".png" or ".jpg" or ".jpeg" or ".webp")
                ?? throw new StageFailedException("The workflow saved no image. It needs a node that saves it, e.g. Save Image.");
            var file = Path.Combine(maker.Folder, "references", $"{reference.Id}-{number}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(picture.Name)}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(maker.Root, file))!);
            await File.WriteAllBytesAsync(Path.Combine(maker.Root, file), picture.Content, cancellationToken);
            Report(context, ActivityKind.Check, $"{name} made");
            return file;
        }
        catch (StageFailedException ex)
        {
            throw new StageFailedException($"{reference.Name}: {ex.Message}");   // the bar says which reference
        }
    }

    /// <summary>What Qwen is asked for: a clean reference of the one named, filled in where part of it is missing.</summary>
    internal static string Prompt(Reference reference, ImageMaking made, string missing)
    {
        var subject = reference.Kind switch
        {
            CastKind.Character => $"{reference.Name}: the whole figure from head to feet, standing, facing the viewer",
            CastKind.Place => $"{reference.Name}: the whole place in a wide view, no people",
            _ => $"{reference.Name}: the whole object alone, centred",
        };
        var keep = made == ImageMaking.FromDescription
            ? ""
            : " Keep the face, shapes, clothes and colours of the source picture exactly.";
        var fill = made == ImageMaking.PartlyMadeUp && missing.Length > 0
            ? $" The source picture does not show {missing}: add them so they match the rest."
            : "";
        var look = reference.Look.Length > 0 ? $" {reference.Look}" : "";
        return $"A clean reference picture of {subject}, on a plain light background, in even light.{keep}{fill}{look}";
    }

    private void Report(StageContext context, ActivityKind kind, string text) =>
        context.Activity.Report(new ActivityLine(clock.GetUtcNow(), kind, text));

    /// <summary>Reports at once on the caller's thread; <see cref="Progress{T}"/> would post to a context.</summary>
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
