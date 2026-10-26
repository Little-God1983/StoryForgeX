using System.Windows;
using System.Windows.Controls;

namespace StoryForge.App.Views;

/// <summary>
/// Lays its children out in a row, each as wide as its share of the weights: on the timeline, a
/// shot's column as wide as the shot lasts.
/// </summary>
public sealed class WeightedPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(WeightedPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(UIElement element) => (double)element.GetValue(WeightProperty);

    public static void SetWeight(UIElement element, double value) => element.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = Widths(availableSize.Width);
        double width = 0, height = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(widths[i], availableSize.Height));
            width += double.IsInfinity(widths[i]) ? child.DesiredSize.Width : widths[i];
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Widths(finalSize.Width);
        var x = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }
        return finalSize;
    }

    /// <summary>Each child's share of <paramref name="width"/>; with no width to share, as wide as it likes.</summary>
    private double[] Widths(double width)
    {
        var weights = InternalChildren.Cast<UIElement>().Select(c => Math.Max(0, GetWeight(c))).ToArray();
        var total = weights.Sum();
        return [.. weights.Select(w => double.IsInfinity(width) ? double.PositiveInfinity : total > 0 ? width * w / total : width / weights.Length)];
    }
}
