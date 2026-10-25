using StoryForge.Client;
using StoryForge.Engine.References;

namespace StoryForge.Engine.Tests;

/// <summary>
/// Who and what gets a reference, and the image pool: pictures from the project's sources, found
/// per reference and on the pages the facts came from.
/// </summary>
public sealed class ReferenceListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Shot Shot(string key, params string[] cast) => new(key, key[..3], 0, 1, 0, 3, "A shot.", cast, false);

    private static readonly StoryboardSheet Sheet = new(
    [
        new CastMember("Karlach", CastKind.Character, ""),
        new CastMember("Soul coin", CastKind.Object, ""),
        new CastMember("Mammon's mint", CastKind.Place, "Where every coin is made."),
        new CastMember("A tavern mug", CastKind.Object, ""),
        new CastMember("Raphael", CastKind.Character, ""),
    ], new Dictionary<string, int>(), Pacing.Default);

    private static readonly IReadOnlyList<Shot> Board =
    [
        Shot("S01/a", "Soul coin"),
        Shot("S01/b", "Karlach", "Soul coin"),
        Shot("S02/a", "Mammon's mint"),
        Shot("S03/a", "Karlach", "A tavern mug"),
    ];

    [Fact]
    public void Shown_in_two_shots_or_once_with_a_reason_makes_a_reference()
    {
        var (references, _) = ReferenceList.Propose(Sheet, Board);

        Assert.Equal(["Karlach", "Soul coin", "Mammon's mint"], references.Select(r => r.Name));
        Assert.Equal(["R01", "R02", "R03"], references.Select(r => r.Id));
        Assert.Equal(["S01/b", "S03/a"], references[0].Shots);
        Assert.Equal(("Where every coin is made.", CastKind.Place), (references[2].KeyReason, references[2].Kind));
        Assert.All(references, r => Assert.Empty(r.Sources));
    }

    [Fact]
    public void The_rest_of_the_cast_is_listed_with_how_often_it_is_shown()
    {
        var (_, others) = ReferenceList.Propose(Sheet, Board);

        Assert.Equal([new OtherCast("A tavern mug", CastKind.Object, 1), new OtherCast("Raphael", CastKind.Character, 0)], others);
    }

    [Fact]
    public void Someone_put_in_a_shot_by_hand_counts_too()
    {
        var (references, _) = ReferenceList.Propose(Sheet, [.. Board, Shot("S04/a", "Nadira"), Shot("S04/b", "Nadira")]);

        Assert.Equal(("Nadira", CastKind.Character), (references[^1].Name, references[^1].Kind));
    }

    private static FoundPicture Picture(string name, int width = 800, int height = 1200, bool main = false, string page = "https://bg3.wiki/wiki/Karlach") =>
        new($"https://bg3.wiki/w/images/{name}", page, "Karlach", name, width, height, main);

    private async Task<(IReadOnlyList<Reference> References, IReadOnlyList<PoolImage> Pool)> BuildAsync(FakePictures pictures, params string[] factPages)
    {
        var (references, _) = ReferenceList.Propose(Sheet, Board);
        var builder = new PoolBuilder(pictures);
        return await builder.BuildAsync(references, [], [], ["bg3.wiki"], factPages, _root, "Soul Coins (1234)", default);
    }

    [Fact]
    public async Task Each_reference_gets_the_pictures_found_for_its_name_the_main_one_first()
    {
        var pictures = new FakePictures();
        pictures.ByName["Karlach"] = [Picture("Camp.jpg", 1920, 1080), Picture("Portrait.png", main: true)];

        var (references, pool) = await BuildAsync(pictures);

        Assert.Equal(["P01", "P02"], pool.Select(p => p.Id).Order());
        // The page's main picture first.
        Assert.Equal(["Portrait.png", "Camp.jpg"], references[0].Sources.Select(id => pool.Single(p => p.Id == id).Caption));
        var camp = pool.Single(p => p.Caption == "Camp.jpg");
        Assert.Equal(Path.Combine("Soul Coins (1234)", "references", "pool", camp.Id + ".jpg"), camp.File);
        Assert.True(File.Exists(Path.Combine(_root, camp.File)));
        Assert.Equal((PoolOrigin.Found, "https://bg3.wiki/wiki/Karlach", 1920), (camp.Origin, camp.Page, camp.Width));
    }

    [Fact]
    public async Task A_picture_too_small_is_kept_with_its_reference_to_say_why_nothing_is_made()
    {
        var pictures = new FakePictures();
        pictures.ByName["Karlach"] = [Picture("Thumb.png", 300, 400, main: true)];

        var (references, pool) = await BuildAsync(pictures);

        Assert.True(Assert.Single(pool).TooSmall);
        Assert.Equal(["P01"], references[0].Sources);
    }

    [Fact]
    public async Task A_picture_that_will_not_download_is_left_out()
    {
        var pictures = new FakePictures();
        pictures.ByName["Karlach"] = [Picture("Gone.png", main: true), Picture("Camp.jpg", 1920, 1080)];
        pictures.Broken.Add("https://bg3.wiki/w/images/Gone.png");

        var (references, pool) = await BuildAsync(pictures);

        Assert.Equal(["Camp.jpg"], pool.Select(p => p.Caption));
        Assert.Equal(["P01"], references[0].Sources);
    }

    [Fact]
    public async Task One_picture_found_twice_is_in_the_pool_once()
    {
        var pictures = new FakePictures();
        pictures.ByName["Karlach"] = [Picture("Coin.png", main: true)];
        pictures.ByName["Soul coin"] = [Picture("Coin.png", main: true, page: "https://bg3.wiki/wiki/Soul_Coin")];

        var (references, pool) = await BuildAsync(pictures);

        Assert.Single(pool);
        Assert.Equal(["P01"], references[0].Sources);
        Assert.Equal(["P01"], references[1].Sources);
    }

    [Fact]
    public async Task The_pages_the_facts_came_from_add_their_pictures_to_the_pool()
    {
        var pictures = new FakePictures();
        pictures.ByPage["https://bg3.wiki/wiki/Infernal_Engine"] = [Picture("Engine.png", page: "https://bg3.wiki/wiki/Infernal_Engine")];

        var (references, pool) = await BuildAsync(pictures, "https://bg3.wiki/wiki/Infernal_Engine");

        Assert.Equal(["Engine.png"], pool.Select(p => p.Caption));
        Assert.All(references, r => Assert.Empty(r.Sources));   // there for you to pick, used by no one yet
    }
}
