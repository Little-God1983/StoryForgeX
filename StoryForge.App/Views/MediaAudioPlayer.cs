using System.Windows.Media;
using System.Windows.Threading;
using StoryForge.App.ViewModels.Script;

namespace StoryForge.App.Views;

/// <summary>Plays audio with WPF's MediaPlayer, and ticks while it plays so the lit word follows.</summary>
public sealed class MediaAudioPlayer : IAudioPlayer
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public MediaAudioPlayer()
    {
        _timer.Tick += (_, _) => Ticked?.Invoke(this, EventArgs.Empty);
        _player.MediaEnded += (_, _) =>
        {
            _timer.Stop();
            _player.Close();
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _player.MediaFailed += (_, e) =>
        {
            _timer.Stop();
            _player.Close();
            Failed?.Invoke(this, e.ErrorException?.Message ?? "the file could not be read");
        };
    }

    public event EventHandler? Ticked;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public TimeSpan Position
    {
        get => _player.Position;
        set => _player.Position = value;
    }

    public void Open(string path) => _player.Open(new Uri(path, UriKind.Absolute));

    public void Play()
    {
        _player.Play();
        _timer.Start();
    }

    public void Pause()
    {
        _player.Pause();
        _timer.Stop();
    }

    public void Close()
    {
        _timer.Stop();
        _player.Close();
    }
}
