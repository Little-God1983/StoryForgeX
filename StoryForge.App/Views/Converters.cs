using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace StoryForge.App.Views;

/// <summary>Collapsed for null (or an empty string), visible otherwise; for error banners.</summary>
public sealed class NullToCollapsedConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}

/// <summary>"C:\…\references\3fa2c1.png" → "3fa2c1.png".</summary>
public sealed class FileNameConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? Path.GetFileName(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}

/// <summary>
/// A small thumbnail of an image file, or null for anything that is not a readable image (a voice
/// profile's reference audio). Loaded fully and closed again, so the file is never held open.
/// </summary>
public sealed class ThumbnailConverter : MarkupExtension, IValueConverter
{
    private static readonly HashSet<string> ImageExtensions =
        new([".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !ImageExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 200;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;   // not decodable: the tile shows the file name instead
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}
