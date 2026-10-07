using System.Windows;

namespace StoryForge.App.ViewModels;

/// <summary>
/// Runs work on the UI thread: at once when already there (or when there is no app, as in tests),
/// else queued to the app's dispatcher. Engine updates arrive on engine threads.
/// </summary>
internal static class UiThread
{
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
