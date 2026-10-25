using StoryForge.Client;
using StoryForge.Engine.Pipeline;
using StoryForge.Engine.Projects;

namespace StoryForge.Engine.References;

/// <summary>
/// What you change on the References screen: the list (add, remove, merge, the look), the pictures
/// a reference is made from, your own pictures, and confirming the list so its images are made.
/// A change to the list is a new version of the stage's sheet; nothing changes while the stage runs.
/// Once the images exist, a change that needs new ones (a reference added, a picture added, a
/// search again, from the description) makes them at once.
/// </summary>
internal sealed class ReferenceEdits(
    ReferenceSheets sheets,
    PipelineRunner runner,
    CellReader cells,
    ProjectStore projects,
    ProjectFolders folders,
    PoolBuilder pool)
{
    private const PipelineStage Stage = PipelineStage.References;

    // One change at a time per project, held from reading the sheet to saving it: a search takes
    // seconds, and a second change meanwhile would save over it or take the same picture id.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    private async Task<IDisposable> LockAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>The list is confirmed: the sheet moves on to its images, and they are made. Past the list, the ones a run never made.</summary>
    public async Task MakeImagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        var sheet = await SheetAsync(projectId, cancellationToken);
        if (sheet.Phase == ReferencePhase.List)
        {
            // An empty list too: nothing is made, and the stage waits to be approved as it is.
            await SaveAsync(projectId, sheet with { Phase = ReferencePhase.Images }, [], cancellationToken);
            await runner.RegenerateAsync(projectId, Stage, cancellationToken);
            return;
        }
        // Past the list: only what a run stopped before is made, references with no image and
        // images never made. The rest keep theirs, approved or not; one is made again from its
        // card. A run that failed is over.
        if (await sheets.ReviewAgainAsync(projectId, cancellationToken))
        {
            runner.Publish(new StageUpdate(projectId, Stage, StageState.NeedsReview));
            await runner.SettleAsync(projectId, Stage, cancellationToken);   // every image may be approved already
        }
        var images = await cells.ReferenceImagesAsync(projectId, cancellationToken);
        var unmade = await cells.UnmadeKeysAsync(projectId, Stage, cancellationToken);
        var keys = sheet.References
            .Where(r => images.All(i => i.ReferenceId != r.Id) && !unmade.Any(k => k.StartsWith(r.Id + "/", StringComparison.Ordinal)))
            .Select(r => ReferenceImage.KeyOf(r.Id, 1))
            .Concat(unmade)
            .Distinct();
        foreach (var key in keys)
        {
            await runner.MakeSegmentAsync(projectId, Stage, key, cancellationToken);
        }
    }

    public async Task ChangeAsync(Guid projectId, ReferenceChange change, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        var project = await projects.GetAsync(projectId, cancellationToken);
        var sheet = await SheetAsync(projectId, cancellationToken);
        var shots = await cells.CurrentShotsAsync(projectId, cancellationToken);
        List<string> removed = [];
        string? remake = null;
        string? fill = null;
        string? thrownOut = null;
        switch (change.Change)
        {
            case ReferenceChangeKind.Add:
            case ReferenceChangeKind.MakeReference:
            {
                var name = (change.Name ?? "").Trim();
                if (name.Length == 0)
                {
                    throw new ArgumentException("A reference needs a name.");
                }
                if (sheet.References.Any(r => ReferenceList.Same(r.Name, name)))
                {
                    throw new ArgumentException($"{name} is on the list already.");
                }
                var kind = change.Kind;
                var reason = "";
                if (change.Change == ReferenceChangeKind.MakeReference)
                {
                    var other = sheet.Others.FirstOrDefault(o => ReferenceList.Same(o.Name, name))
                        ?? throw new KeyNotFoundException($"{name} is not on the rest of the cast.");
                    var board = await cells.CurrentStoryboardAsync(projectId, cancellationToken);
                    (name, kind, reason) = (other.Name, other.Kind, board?.Cast.FirstOrDefault(c => ReferenceList.Same(c.Name, name))?.KeyReason ?? "");
                }
                var reference = new Reference(
                    ReferenceList.NextId(sheet.References), name, kind, reason, "",
                    [.. shots.Where(s => s.Cast.Any(n => ReferenceList.Same(n, name))).Select(s => s.Key)], []);
                sheet = sheet with
                {
                    References = [.. sheet.References, reference],
                    Others = [.. sheet.Others.Where(o => !ReferenceList.Same(o.Name, name))],
                };
                // Searched at once, in either phase: a reference must not say "no picture found" unsearched.
                sheet = await SearchAsync(project, sheet, reference.Id, cancellationToken);
                remake = reference.Id;
                break;
            }
            case ReferenceChangeKind.Remove:
            {
                var reference = Find(sheet, change.ReferenceId);
                sheet = sheet with
                {
                    References = [.. sheet.References.Where(r => r.Id != reference.Id)],
                    // Back with the rest of the cast, so "Make it a reference" takes it back.
                    Others = [.. sheet.Others.Where(o => !ReferenceList.Same(o.Name, reference.Name)), new OtherCast(reference.Name, reference.Kind, reference.Shots.Count)],
                };
                removed.Add(reference.Id);
                break;
            }
            case ReferenceChangeKind.Merge:
            {
                var from = Find(sheet, change.ReferenceId);
                var into = Find(sheet, change.OtherId);
                if (from.Id == into.Id)
                {
                    throw new ArgumentException($"{from.Name} cannot be merged with itself.");
                }
                bool Usable(string id) => sheet.Pool.Any(p => p.Id == id && !p.TooSmall);
                var merged = into with
                {
                    Shots = [.. into.Shots.Concat(from.Shots).Distinct()],
                    Sources = [.. into.Sources.Concat(from.Sources).Distinct()],
                    Chosen = [.. (into.Chosen ?? []).Concat(from.Chosen ?? []).Distinct()],
                    Dropped = [.. (into.Dropped ?? []).Concat(from.Dropped ?? []).Except(into.Sources.Concat(from.Sources)).Distinct()],
                    Look = into.Look.Length > 0 ? into.Look : from.Look,
                    KeyReason = into.KeyReason.Length > 0 ? into.KeyReason : from.KeyReason,
                    // Made from its look alone, as the one merged in was, when there is still no picture to make it from.
                    DescriptionOnly = into.DescriptionOnly || (from.DescriptionOnly && !into.Sources.Concat(from.Sources).Any(Usable)),
                };
                sheet = sheet with { References = [.. sheet.References.Where(r => r.Id != from.Id).Select(r => r.Id == into.Id ? merged : r)] };
                removed.Add(from.Id);
                // Its images stay; those that show nothing are made again when the merge brought something to make them from.
                if (from.Sources.Except(into.Sources).Any(Usable) || merged.DescriptionOnly != into.DescriptionOnly)
                {
                    fill = into.Id;
                }
                break;
            }
            case ReferenceChangeKind.EditLook:
            {
                var reference = Find(sheet, change.ReferenceId);
                var look = (change.Look ?? "").Trim();
                if (look.Length == 0 && reference.DescriptionOnly)
                {
                    throw new ArgumentException($"{reference.Name} is made from its look alone: write how it looks, or pick a picture for it first.");
                }
                sheet = Replace(sheet, reference with { Look = look });
                break;
            }
            case ReferenceChangeKind.UseSource:
            {
                var reference = Find(sheet, change.ReferenceId);
                var picture = FindPicture(sheet, change.PoolImageId);
                if (picture.TooSmall)
                {
                    throw new ArgumentException(
                        $"{picture.Id} is {picture.Width} × {picture.Height} px: an image is made only from a picture of at least {PoolImage.MinUsableSide} px on each side.");
                }
                // First, so it is what the image is made from, and made from at once once there are images.
                sheet = Replace(sheet, reference with
                {
                    Sources = [picture.Id, .. reference.Sources.Where(id => id != picture.Id)],
                    Chosen = [.. (reference.Chosen ?? []).Append(picture.Id).Distinct()],
                    Dropped = [.. (reference.Dropped ?? []).Where(id => id != picture.Id)],
                    DescriptionOnly = false,
                });
                remake = reference.Id;
                break;
            }
            case ReferenceChangeKind.DropSource:
            {
                var reference = Find(sheet, change.ReferenceId);
                var picture = FindPicture(sheet, change.PoolImageId);
                sheet = Replace(sheet, reference with
                {
                    Sources = [.. reference.Sources.Where(id => id != picture.Id)],
                    Chosen = [.. (reference.Chosen ?? []).Where(id => id != picture.Id)],
                    Dropped = [.. (reference.Dropped ?? []).Append(picture.Id).Distinct()],
                });
                break;
            }
            case ReferenceChangeKind.DiscardFromPool:
            {
                var picture = FindPicture(sheet, change.PoolImageId);
                sheet = sheet with
                {
                    Pool = [.. sheet.Pool.Where(p => p.Id != picture.Id)],
                    Discarded = [.. sheet.Discarded ?? [], picture],
                    References = [.. sheet.References.Select(r => r with { Sources = [.. r.Sources.Where(id => id != picture.Id)], Chosen = [.. (r.Chosen ?? []).Where(id => id != picture.Id)] })],
                };
                thrownOut = Path.Combine(await folders.RootAsync(cancellationToken), picture.File);
                break;
            }
            case ReferenceChangeKind.RemoveImage:
            {
                var key = change.ImageKey ?? "";
                var reference = Find(sheet, key.Split('/')[0]);
                var made = (await cells.ReferenceImagesAsync(projectId, cancellationToken)).Select(i => ReferenceImage.KeyOf(i.ReferenceId, i.Number));
                var keys = made.Concat(await cells.UnmadeKeysAsync(projectId, Stage, cancellationToken))
                    .Where(k => k.StartsWith(reference.Id + "/", StringComparison.Ordinal))
                    .Distinct()
                    .ToList();
                if (!keys.Contains(key))
                {
                    throw new KeyNotFoundException($"{reference.Name} has no image {key}.");
                }
                if (keys.Count < 2)
                {
                    throw new InvalidOperationException($"{reference.Name} keeps at least one image: make it again instead.");
                }
                removed.Add(key);
                break;
            }
            default:
                throw new ArgumentException($"There is no change \"{change.Change}\".");
        }
        await SaveAsync(projectId, sheet, removed, cancellationToken);
        if (thrownOut is not null)
        {
            Voice.VoiceStage.DeleteQuietly(thrownOut);   // once the sheet no longer names it
        }
        if (remake is not null)
        {
            await MakeImagesOfAsync(projectId, remake, cancellationToken);
        }
        if (fill is not null)
        {
            await MakeImagesOfAsync(projectId, fill, cancellationToken, onlyEmpty: true);
        }
        // The rest may all be approved now; or an approved stage got a reference that still needs its image.
        await runner.SettleAsync(projectId, Stage, cancellationToken);
    }

    /// <summary>Your own picture: copied into the pool and used first for the reference, which is made again from it.</summary>
    public async Task AddOwnPictureAsync(Guid projectId, string referenceId, string path, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file {path}.", path);
        }
        var project = await projects.GetAsync(projectId, cancellationToken);
        var sheet = await SheetAsync(projectId, cancellationToken);
        var reference = Find(sheet, referenceId);
        var content = await File.ReadAllBytesAsync(path, cancellationToken);
        if (ImageSize.Of(content) is not { } size || ImageSize.Ending(content) is not { } ending)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is not a PNG, JPEG or WebP picture.");
        }
        if (Math.Min(size.Width, size.Height) < PoolImage.MinUsableSide)
        {
            throw new ArgumentException(
                $"{Path.GetFileName(path)} is {size.Width} × {size.Height} px: an image is made only from a picture of at least {PoolImage.MinUsableSide} px on each side.");
        }
        var id = PoolBuilder.NextId(sheet.Pool.Concat(sheet.Discarded ?? []));
        var file = Path.Combine(ProjectFolders.Of(project), "references", "pool", id + ending);
        var target = Path.Combine(await folders.RootAsync(cancellationToken), file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, content, cancellationToken);
        var picture = new PoolImage(id, Path.GetFileName(path), "", "", size.Width, size.Height, file, PoolOrigin.Own);
        sheet = Replace(sheet with { Pool = [.. sheet.Pool, picture] }, reference with { Sources = [id, .. reference.Sources], DescriptionOnly = false });
        await SaveAsync(projectId, sheet, [], cancellationToken);
        await MakeImagesOfAsync(projectId, referenceId, cancellationToken);
    }

    /// <summary>Searches the sources for the reference again; it is made from what is found, and your own pictures.</summary>
    public async Task SearchAgainAsync(Guid projectId, string referenceId, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        var project = await projects.GetAsync(projectId, cancellationToken);
        var sheet = await SheetAsync(projectId, cancellationToken);
        var before = Find(sheet, referenceId);
        sheet = await SearchAsync(project, sheet, referenceId, cancellationToken);
        // Nothing new found, e.g. while the wiki cannot be reached: what it had stays, and its images.
        var found = Find(sheet, referenceId).Sources.Where(id => !before.Sources.Contains(id) && sheet.Pool.Any(p => p.Id == id && p.Origin == PoolOrigin.Found));
        if (!found.Any())
        {
            throw new InvalidOperationException($"Nothing new was found for {before.Name} on the sources just now. It keeps the pictures it has.");
        }
        await SaveAsync(projectId, sheet, [], cancellationToken);
        await MakeImagesOfAsync(projectId, referenceId, cancellationToken);
    }

    /// <summary>From its look alone: the one case where text alone is allowed.</summary>
    public async Task FromDescriptionAsync(Guid projectId, string referenceId, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        var sheet = await SheetAsync(projectId, cancellationToken);
        var reference = Find(sheet, referenceId);
        if (reference.Look.Length == 0)
        {
            throw new ArgumentException($"Write how {reference.Name} looks first (Edit look…): that is all it is made from.");
        }
        await SaveAsync(projectId, Replace(sheet, reference with { DescriptionOnly = true }), [], cancellationToken);
        await MakeImagesOfAsync(projectId, referenceId, cancellationToken);
    }

    /// <summary>"+ image": the reference's next image, made from its pictures.</summary>
    public async Task AddImageAsync(Guid projectId, string referenceId, CancellationToken cancellationToken)
    {
        using var held = await LockAsync(projectId, cancellationToken);
        RequireIdle(projectId);
        var sheet = await SheetAsync(projectId, cancellationToken);
        var reference = Find(sheet, referenceId);
        if (sheet.Phase != ReferencePhase.Images)
        {
            throw new InvalidOperationException("Make the images first: the list is still waiting for you.");
        }
        // Another image with nothing to make it from could never be approved, and there is no taking it back.
        if (!reference.DescriptionOnly && !reference.Sources.Any(id => sheet.Pool.Any(p => p.Id == id && !p.TooSmall)))
        {
            throw new InvalidOperationException(
                $"There is no picture to make another image of {reference.Name} from. Search the sources again, add your own picture, or make it from the description.");
        }
        var images = await cells.ReferenceImagesAsync(projectId, cancellationToken);
        var next = 1 + images.Where(i => i.ReferenceId == referenceId).Select(i => i.Number).DefaultIfEmpty(0).Max();
        await runner.MakeSegmentAsync(projectId, Stage, ReferenceImage.KeyOf(referenceId, next), cancellationToken);
    }

    /// <summary>
    /// Once the images exist: every image of the reference made again (with <paramref name="onlyEmpty"/>,
    /// those that show nothing), those never made made, or its first one made.
    /// </summary>
    private async Task MakeImagesOfAsync(Guid projectId, string referenceId, CancellationToken cancellationToken, bool onlyEmpty = false)
    {
        if ((await SheetAsync(projectId, cancellationToken)).Phase != ReferencePhase.Images)
        {
            return;
        }
        var images = await cells.ReferenceImagesAsync(projectId, cancellationToken);
        var own = images.Where(i => i.ReferenceId == referenceId).ToList();
        var unmade = (await cells.UnmadeKeysAsync(projectId, Stage, cancellationToken)).Where(k => k.StartsWith(referenceId + "/", StringComparison.Ordinal)).ToList();
        if (own.Count == 0 && unmade.Count == 0)
        {
            await runner.MakeSegmentAsync(projectId, Stage, ReferenceImage.KeyOf(referenceId, 1), cancellationToken);
            return;
        }
        foreach (var number in own.Where(i => !onlyEmpty || i.Made == ImageMaking.NoPicture).Select(i => i.Number))
        {
            await runner.RegenerateSegmentAsync(projectId, Stage, ReferenceImage.KeyOf(referenceId, number), cancellationToken);
        }
        foreach (var key in unmade)
        {
            await runner.MakeSegmentAsync(projectId, Stage, key, cancellationToken);
        }
    }

    /// <summary>The pictures the sources have now for one reference; your own pictures and those you picked stay with it.</summary>
    private async Task<ReferenceSheet> SearchAsync(Project project, ReferenceSheet sheet, string referenceId, CancellationToken cancellationToken)
    {
        var reference = sheet.References.First(r => r.Id == referenceId);
        // Your own pictures and the ones you picked stay; the rest is what the sources have now.
        var own = reference.Sources.Where(id => (reference.Chosen ?? []).Contains(id) || sheet.Pool.Any(p => p.Id == id && p.Origin == PoolOrigin.Own)).ToList();
        var (found, images) = await pool.BuildAsync(
            [reference with { Sources = own }], sheet.Pool, sheet.Discarded ?? [], project.Setup.ResearchSources, [],
            await folders.RootAsync(cancellationToken), ProjectFolders.Of(project), cancellationToken);
        // What you took off it stays off.
        return Replace(sheet with { Pool = images }, found[0] with { Sources = [.. found[0].Sources.Except(reference.Dropped ?? [])], DescriptionOnly = false });
    }

    private static ReferenceSheet Replace(ReferenceSheet sheet, Reference reference) =>
        sheet with { References = [.. sheet.References.Select(r => r.Id == reference.Id ? reference : r)] };

    private static Reference Find(ReferenceSheet sheet, string? id) =>
        sheet.References.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException($"There is no reference {id}.");

    private static PoolImage FindPicture(ReferenceSheet sheet, string? id) =>
        sheet.Pool.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"There is no picture {id} in the pool.");

    private async Task<ReferenceSheet> SheetAsync(Guid projectId, CancellationToken cancellationToken) =>
        await cells.CurrentReferencesAsync(projectId, cancellationToken)
        ?? throw new KeyNotFoundException($"Project {projectId} has no reference list yet.");

    /// <exception cref="InvalidOperationException">The References stage, or one of its images, is running.</exception>
    private void RequireIdle(Guid projectId)
    {
        if (runner.IsAnyBusy(projectId, Stage))
        {
            throw new InvalidOperationException("The references are being made. Try again when they are done.");
        }
    }

    /// <summary>
    /// The changed sheet as the stage's next version. Checked again here: a search takes seconds,
    /// and a run of the stage started meanwhile would write over it.
    /// </summary>
    private async Task SaveAsync(Guid projectId, ReferenceSheet sheet, IReadOnlyList<string> removed, CancellationToken cancellationToken)
    {
        RequireIdle(projectId);
        var (keys, state) = await sheets.WriteAsync(projectId, sheet, removed, cancellationToken);
        foreach (var key in keys)
        {
            runner.Publish(new StageUpdate(projectId, Stage, StageState.NotStarted, Key: key));
        }
        runner.Publish(new StageUpdate(projectId, Stage, state));
    }
}
