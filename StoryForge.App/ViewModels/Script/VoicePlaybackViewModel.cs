using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.Script;

/// <summary>Plays one audio file; the app's is WPF's MediaPlayer, tests use their own.</summary>
public interface IAudioPlayer
{
    /// <summary>Opens a file, ready to play from the start; the file before it is closed.</summary>
    void Open(string path);

    void Play();

    void Pause();

    /// <summary>Stops and lets go of the file.</summary>
    void Close();

    TimeSpan Position { get; set; }

    /// <summary>The position moved while playing, a few times a second.</summary>
    event EventHandler? Ticked;

    /// <summary>Played to the end.</summary>
    event EventHandler? Ended;

    /// <summary>The file could not be played; the reason in plain words.</summary>
    event EventHandler<string>? Failed;
}

/// <summary>One word of the narration: lit while it is heard, so the estimated timings can be checked by ear.</summary>
public sealed partial class SpokenWordViewModel(SpokenWord word, Action<SpokenWordViewModel> seek) : ObservableObject
{
    public string Text => word.Text;

    public double Start => word.Start;

    public double End => word.End;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>"0:12.4": when the word starts, as a tooltip.</summary>
    public string StartText => string.Create(CultureInfo.InvariantCulture, $"{(int)Start / 60}:{Start % 60:00.0}");

    /// <summary>Plays from this word.</summary>
    [RelayCommand]
    private void Seek() => seek(this);

    public override string ToString() => Text;
}

/// <summary>
/// The Voice panel's player: Play / Pause, where it is, and the words with the one being heard lit.
/// Click a word to hear from there.
/// </summary>
public sealed partial class VoicePlaybackViewModel : ObservableObject
{
    private readonly IAudioPlayer _player;
    private bool _opened;

    public VoicePlaybackViewModel(IAudioPlayer player)
    {
        _player = player;
        _player.Ticked += (_, _) => Follow(_player.Position.TotalSeconds);
        _player.Ended += (_, _) =>
        {
            IsPlaying = false;
            _opened = false;   // the next Play starts from the beginning
            Follow(0);
            Light(null);
        };
        _player.Failed += (_, reason) =>
        {
            IsPlaying = false;
            _opened = false;
            Error = $"Could not play the audio: {reason}";
        };
    }

    public ObservableCollection<SpokenWordViewModel> Words { get; } = [];

    /// <summary>The file playing, or ready to; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(IsMissing))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    private string? _path;

    /// <summary>The segment has audio, but the file is gone from the projects folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    private bool _hasClip;

    public bool CanPlay => Path is not null;

    public bool IsMissing => HasClip && Path is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool _isPlaying;

    /// <summary>"Play" or "Pause".</summary>
    public string PlayLabel => IsPlaying ? "Pause" : "Play";

    [ObservableProperty]
    private double _seconds;

    /// <summary>"0:12 / 0:31".</summary>
    [ObservableProperty]
    private string _positionText = "";

    [ObservableProperty]
    private string? _error;

    /// <summary>Shows a segment's audio and words; whatever played before stops.</summary>
    public void Show(VoiceSegmentView? segment)
    {
        Stop();
        Error = null;
        HasClip = segment is not null;
        Path = segment?.AudioPath;
        Seconds = segment?.Seconds ?? 0;
        Words.Clear();
        foreach (var word in segment?.Words ?? [])
        {
            Words.Add(new SpokenWordViewModel(word, SeekTo));
        }
        Follow(0);
    }

    /// <summary>Stops playing and lets go of the file, e.g. when another cell is selected.</summary>
    public void Stop()
    {
        if (_opened)
        {
            _player.Close();
            _opened = false;
        }
        IsPlaying = false;
        Light(null);
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }
        Play(at: null);
    }

    private void SeekTo(SpokenWordViewModel word)
    {
        if (CanPlay)
        {
            Play(at: word.Start);
        }
    }

    private void Play(double? at)
    {
        try
        {
            if (!_opened)
            {
                _player.Open(Path!);
                _opened = true;
            }
            if (at is { } start)
            {
                _player.Position = TimeSpan.FromSeconds(start);
            }
            _player.Play();
            Error = null;
            IsPlaying = true;
            Follow(_player.Position.TotalSeconds);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Playing {Path} failed: {ex}");
            _opened = false;
            Error = $"Could not play the audio: {ex.Message}";
        }
    }

    /// <summary>The playhead is at <paramref name="seconds"/>: the word heard there is lit.</summary>
    private void Follow(double seconds)
    {
        PositionText = $"{Clock(seconds)} / {Clock(Seconds)}";
        if (IsPlaying)
        {
            Light(Words.LastOrDefault(w => w.Start <= seconds));
        }
    }

    private void Light(SpokenWordViewModel? current)
    {
        foreach (var word in Words)
        {
            word.IsCurrent = word == current;
        }
    }

    private static string Clock(double seconds)
    {
        var whole = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }
}
