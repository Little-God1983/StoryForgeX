using System.Windows;
using System.Windows.Threading;

namespace StoryForge.App.Views;

/// <summary>Moves the keyboard into an element each time it appears, e.g. the note box Regenerate opens.</summary>
public static class FocusWhenShown
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FocusWhenShown), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is UIElement shown && e.NewValue is true)
        {
            shown.IsVisibleChanged += (_, visible) =>
            {
                if (visible.NewValue is true)
                {
                    shown.Dispatcher.BeginInvoke(() => shown.Focus(), DispatcherPriority.Input);
                }
            };
        }
    }
}
