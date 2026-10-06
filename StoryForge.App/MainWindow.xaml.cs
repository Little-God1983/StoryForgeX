using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StoryForge.App.Interop;
using StoryForge.App.ViewModels;

namespace StoryForge.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Closing += OnClosing;
    }

    public MainViewModel ViewModel { get; }

    private bool _flushedBeforeClose;

    // Settings save a moment after the last keystroke; closing first writes what is still waiting.
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_flushedBeforeClose)
        {
            return;
        }
        e.Cancel = true;
        try
        {
            await ViewModel.FlushAsync();
        }
        finally
        {
            _flushedBeforeClose = true;
            Close();
        }
    }

    /// <summary>
    /// Ctrl+click on the selected item clears a single-select ListBox, which would leave a screen
    /// shown with no item highlighted. The highlight is put back once the selection change has
    /// finished; inside SelectionChanged the list ignores a new selection. Doing this in the view
    /// model does not work: WPF leaves the item container unselected when the source changes
    /// while the binding is writing it (checked on screen, 2026-10-06).
    /// </summary>
    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0 && sender is ListBox list)
        {
            var removed = e.RemovedItems[0];
            Dispatcher.BeginInvoke(() =>
            {
                if (list.SelectedItem is null)
                {
                    list.SelectedItem = removed;
                }
            });
        }
    }
}
