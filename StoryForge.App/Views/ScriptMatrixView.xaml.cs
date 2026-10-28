using System.Windows;
using System.Windows.Controls;

namespace StoryForge.App.Views;

public partial class ScriptMatrixView : UserControl
{
    public ScriptMatrixView()
    {
        InitializeComponent();
    }

    // The Clip panel's player: a view-only matter, so it lives here. A clip opens on its first frame
    // and loops while it plays.

    private void ClipOpened(object sender, RoutedEventArgs e)
    {
        ClipPlayer.Position = TimeSpan.Zero;
        ClipPlayer.Pause();
    }

    private void ClipEnded(object sender, RoutedEventArgs e)
    {
        ClipPlayer.Position = TimeSpan.Zero;
        ClipPlayer.Play();
    }

    private void PlayClip(object sender, RoutedEventArgs e) => ClipPlayer.Play();

    private void PauseClip(object sender, RoutedEventArgs e) => ClipPlayer.Pause();
}
