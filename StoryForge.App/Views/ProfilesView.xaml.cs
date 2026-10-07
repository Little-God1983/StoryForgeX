using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using StoryForge.App.ViewModels.Profiles;

namespace StoryForge.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    // The name box appears with the click; it takes the focus once it is shown.
    private void NewProfile_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() => NewNameBox.Focus(), DispatcherPriority.Input);

    private async void AddReferenceFiles_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileEditorViewModel editor)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = editor.Draft.ReferenceFilesTitle,
            Multiselect = true,
            Filter = editor.Draft.IsVoice
                ? "Audio|*.wav;*.mp3;*.flac;*.ogg|All files|*.*"
                : "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await editor.AddReferenceFilesAsync(dialog.FileNames);
        }
    }

    // Same Ctrl+click guard as the main navigation: the open profile stays selected.
    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
