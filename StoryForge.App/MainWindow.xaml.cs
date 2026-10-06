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
    }

    public MainViewModel ViewModel { get; }

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
