using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace StoryForge.App.Views;

public partial class ScriptMatrixView : UserControl
{
    // Play was clicked for the clip shown; a new clip shows its first frame until it is.
    private bool _playing;

    public ScriptMatrixView()
    {
        InitializeComponent();
        // A manual player opens its media only once told to play or pause: pausing on a new clip
        // opens it, and shows its first frame.
        DependencyPropertyDescriptor.FromProperty(MediaElement.SourceProperty, typeof(MediaElement)).AddValueChanged(ClipPlayer, (_, _) =>
        {
            _playing = false;
            if (ClipPlayer.Source is not null)
            {
                ClipPlayer.Pause();
            }
        });
    }

    // The Clip panel's player: a view-only matter, so it lives here. It loops while it plays.

    private void ClipOpened(object sender, RoutedEventArgs e)
    {
        if (!_playing)
        {
            ClipPlayer.Position = TimeSpan.Zero;
        }
    }

    private void ClipEnded(object sender, RoutedEventArgs e)
    {
        ClipPlayer.Position = TimeSpan.Zero;
        ClipPlayer.Play();
    }

    private void PlayClip(object sender, RoutedEventArgs e)
    {
        _playing = true;
        ClipPlayer.Play();
    }

    private void PauseClip(object sender, RoutedEventArgs e)
    {
        _playing = false;
        ClipPlayer.Pause();
    }
}
