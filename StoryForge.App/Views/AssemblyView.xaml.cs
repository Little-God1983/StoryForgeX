using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StoryForge.App.ViewModels.Assembly;

namespace StoryForge.App.Views;

public partial class AssemblyView : UserControl
{
    private static readonly DependencyPropertyDescriptor Source = DependencyPropertyDescriptor.FromProperty(MediaElement.SourceProperty, typeof(MediaElement));

    private AssemblyViewModel? _screen;

    public AssemblyView()
    {
        InitializeComponent();
        // Only while shown: the descriptor and the screen's event would otherwise hold the view for good.
        Loaded += (_, _) =>
        {
            Source.AddValueChanged(Player, SourceChanged);
            Watch(DataContext as AssemblyViewModel);
            // Back on a screen whose video was bound before it loaded: open it, as a new one would be.
            SourceChanged(Player, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            Source.RemoveValueChanged(Player, SourceChanged);
            Watch(null);
        };
        DataContextChanged += (_, e) => Watch(e.NewValue as AssemblyViewModel);
    }

    private void Watch(AssemblyViewModel? screen)
    {
        if (_screen is not null)
        {
            _screen.SeekRequested -= Seek;
        }
        _screen = screen;
        if (screen is not null)
        {
            screen.SeekRequested += Seek;
        }
    }

    // A manual player opens its media only once told to play or pause: pausing on a new video opens
    // it, and shows its first frame.
    private void SourceChanged(object? sender, EventArgs e)
    {
        if (Player.Source is not null)
        {
            Player.Pause();
        }
    }

    private void VideoOpened(object sender, RoutedEventArgs e) => Player.Position = TimeSpan.Zero;

    // Picking a cut shows the moment just before it.
    private void Seek(object? sender, TimeSpan at)
    {
        if (Player.Source is not null)
        {
            Player.Pause();
            Player.Position = at;
        }
    }

    private void PlayVideo(object sender, RoutedEventArgs e) => Player.Play();

    private void PauseVideo(object sender, RoutedEventArgs e) => Player.Pause();
}
