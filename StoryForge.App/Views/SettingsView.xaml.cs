using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StoryForge.App.ViewModels.Pages;

namespace StoryForge.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel Page => (SettingsPageViewModel)DataContext;

    // PasswordBox.Password is deliberately not bindable; the token goes straight to the
    // credential store and the box is cleared.
    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (FindTokenBox(sender) is { } box && box.Password.Length > 0)
        {
            await Page.LmStudio.SaveApiTokenAsync(box.Password);
            box.Clear();
        }
    }

    private async void RemoveToken_Click(object sender, RoutedEventArgs e) => await Page.LmStudio.RemoveApiTokenAsync();

    private void BrowseProjectsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Projects folder", InitialDirectory = Page.EffectiveProjectsFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Page.ProjectsFolder = dialog.FolderName;
        }
    }

    // Same Ctrl+click guard as the main navigation: a section always stays selected.
    private void Sections_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    // The token box lives in the LM Studio card's template, next to the clicked button.
    private static PasswordBox? FindTokenBox(object sender) =>
        sender is DependencyObject button && VisualTreeHelper.GetParent(button) is Panel row
            ? row.Children.OfType<PasswordBox>().FirstOrDefault()
            : null;
}
