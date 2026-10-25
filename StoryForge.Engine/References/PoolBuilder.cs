using StoryForge.Client;

namespace StoryForge.Engine.References;

/// <summary>A picture a source page shows, before it is downloaded into the pool.</summary>
internal sealed record FoundPicture(string Url, string Page, string PageTitle, string Caption, int Width, int Height, bool IsMain);

/// <summary>A downloaded picture and the file ending its type calls for (".png").</summary>
internal sealed record DownloadedPicture(byte[] Content, string Extension);

/// <summary>Where pictures come from: the project's sources, and nothing else.</summary>
internal interface IPictureSource
{
    /// <summary>The pictures of the page that best matches <paramref name="name"/> on one of the sources; none when no page does.</summary>
    Task<IReadOnlyList<FoundPicture>> SearchAsync(IReadOnlyList<string> sources, string name, CancellationToken cancellationToken);

    /// <summary>The pictures of one page; none when it is not on a source, not a wiki page, or gone.</summary>
    Task<IReadOnlyList<FoundPicture>> PageAsync(IReadOnlyList<string> sources, string pageUrl, CancellationToken cancellationToken);

    /// <summary>The picture itself; null when it cannot be had or is no picture.</summary>
    Task<DownloadedPicture?> DownloadAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// The image pool: the pictures found for each reference's name and on the pages the facts came
/// from, downloaded into the project folder. A reference is made from its own pictures, the page's
/// main picture first; a picture too small to work from stays in the pool and makes nothing.
/// </summary>
internal sealed class PoolBuilder(IPictureSource pictures)
{
    /// <param name="references">The references to find pictures for; their sources are added to.</param>
    /// <param name="pool">The pool so far: a picture already in it is not downloaded again.</param>
    /// <param name="root">The projects folder.</param>
    /// <param name="folder">The project's folder within it.</param>
    public async Task<(IReadOnlyList<Reference> References, IReadOnlyList<PoolImage> Pool)> BuildAsync(
        IReadOnlyList<Reference> references,
        IReadOnlyList<PoolImage> pool,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> factPages,
        string root,
        string folder,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var images = pool.ToList();
        var updated = new List<Reference>();
        foreach (var reference in references)
        {
            progress?.Report($"looking for pictures of {reference.Name}");
            var found = await pictures.SearchAsync(sources, reference.Name, cancellationToken);
            var ids = await AddAsync(images, found.OrderByDescending(p => p.IsMain), root, folder, cancellationToken);
            var usable = ids.Where(id => !images.First(i => i.Id == id).TooSmall);
            updated.Add(reference with { Sources = [.. reference.Sources.Concat(usable).Distinct()] });
        }
        foreach (var page in factPages.Distinct())
        {
            progress?.Report($"pictures on {page}");
            await AddAsync(images, await pictures.PageAsync(sources, page, cancellationToken), root, folder, cancellationToken);
        }
        return (updated, images);
    }

    /// <summary>Downloads what is not in the pool yet; returns the pool ids of all of them, in order.</summary>
    private async Task<List<string>> AddAsync(List<PoolImage> pool, IEnumerable<FoundPicture> found, string root, string folder, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        foreach (var picture in found)
        {
            if (pool.FirstOrDefault(p => p.Url == picture.Url) is { } known)
            {
                ids.Add(known.Id);
                continue;
            }
            if (await pictures.DownloadAsync(picture.Url, cancellationToken) is not { } download)
            {
                continue;   // gone, or not a picture: the rest of the pool stays
            }
            var id = NextId(pool);
            var file = Path.Combine(folder, "references", "pool", id + download.Extension);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
            await File.WriteAllBytesAsync(Path.Combine(root, file), download.Content, cancellationToken);
            pool.Add(new PoolImage(id, picture.Url, picture.Page, picture.Caption, picture.Width, picture.Height, file, PoolOrigin.Found));
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>"P01" for the first picture; one past the highest in use.</summary>
    public static string NextId(IEnumerable<PoolImage> pool) =>
        $"P{1 + pool.Select(p => int.TryParse(p.Id.AsSpan(1), out var n) ? n : 0).DefaultIfEmpty(0).Max():00}";
}
