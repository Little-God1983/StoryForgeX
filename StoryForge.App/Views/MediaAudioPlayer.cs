using System.Windows.Media;
using System.Windows.Threading;
using StoryForge.App.ViewModels.Script;

namespace StoryForge.App.Views;

/// <summary>Plays audio with WPF's MediaPlayer, and ticks while it plays so the lit word follows.</summary>
public sealed class MediaAudioPlayer : IAudioPlayer
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // MediaPlayer opens a file in the background, and a position set before that is lost: it is
    // kept here and set once the file is open.
    private bool _isOpen;
    private TimeSpan? _seek;

    public MediaAudioPlayer()
    {
        _timer.Tick += (_, _) => Ticked?.Invoke(this, EventArgs.Empty);
        _player.MediaOpened += (_, _) =>
        {
            _isOpen = true;
            if (_seek is { } at)
            {
                _player.Position = at;
                _seek = null;
            }
        };
        _player.MediaEnded += (_, _) =>
        {
            _timer.Stop();
            _isOpen = false;
            _player.Close();
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _player.MediaFailed += (_, e) =>
        {
            _timer.Stop();
            _isOpen = false;
            _player.Close();
            Failed?.Invoke(this, e.ErrorException?.Message ?? "the file could not be read");
        };
    }

    public event EventHandler? Ticked;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public TimeSpan Position
    {
        get => _seek ?? _player.Position;
        set
        {
            if (_isOpen)
            {
                _player.Position = value;
            }
            else
            {
                _seek = value;
            }
        }
    }

    public void Open(string path)
    {
        _isOpen = false;
        _seek = null;
        _player.Open(new Uri(path, UriKind.Absolute));
    }

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
        _isOpen = false;
        _seek = null;
        _player.Close();
    }
}
