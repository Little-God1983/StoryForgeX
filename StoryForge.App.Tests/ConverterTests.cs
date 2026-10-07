using System.Globalization;
using System.IO;
using StoryForge.App.Views;

namespace StoryForge.App.Tests;

public sealed class ConverterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "StoryForgeX.Tests", Guid.NewGuid().ToString("N"));

    public ConverterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_broken_image_gets_no_thumbnail_instead_of_an_error()
    {
        // A half-downloaded PNG: a valid signature, then nothing.
        var path = Path.Combine(_folder, "ref.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00]);

        var thumbnail = new ThumbnailConverter().Convert(path, typeof(object), null, CultureInfo.InvariantCulture);

        Assert.Null(thumbnail);
    }

    [Fact]
    public void Audio_gets_no_thumbnail()
    {
        var path = Path.Combine(_folder, "voice.wav");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(new ThumbnailConverter().Convert(path, typeof(object), null, CultureInfo.InvariantCulture));
    }
}
