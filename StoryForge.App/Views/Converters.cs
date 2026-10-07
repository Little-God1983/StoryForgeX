using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace StoryForge.App.Views;

/// <summary>A moment as the local time of day, "15:40:02"; for activity logs.</summary>
public sealed class LocalTimeConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset moment ? moment.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}

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
        catch (Exception)
        {
            // Anything a broken file throws (FileFormatException, COMException, IOException …):
            // a converter that throws ends up in the app-wide error box on every render, so the
            // tile shows the file name instead.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}

/// <summary>
/// Checks a radio button when the bound value equals ConverterParameter; checking it writes the
/// parameter back. Unchecking writes nothing (the newly checked button writes its own value).
/// </summary>
public sealed class ValueEqualsConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}

/// <summary>"Still images" → "STILL IMAGES", for card and section labels (WPF has no text-transform).</summary>
public sealed class UpperCaseConverter : MarkupExtension, IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(culture) ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public override object ProvideValue(IServiceProvider serviceProvider) => this;
}
