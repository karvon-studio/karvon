using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Machine.Media;
using Karvon.ViewModels;

namespace Karvon.Machine.ViewModels;

/// <summary>A picture, a video or a sound opened from This Computer, as a document of its own.</summary>
public sealed class MachineFileDocument : DocumentTab
{
    public MachineFileDocument(string path, object page)
    {
        Key = "machine-file:" + path;
        Content = page;
        if (page is ImagePage image)
            image.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImagePage.Name))
                    OnPropertyChanged(nameof(Title));
            };
    }

    /// <summary>Its page: <see cref="ImagePage"/> or <see cref="MediaPage"/>.</summary>
    public object Content { get; }

    public override string Key { get; }

    public override string Title => Content switch
    {
        ImagePage image => image.Name,
        MediaPage media => media.Name,
        _ => "",
    };

    public override string ToolTip => Content switch
    {
        ImagePage image => image.Path,
        MediaPage media => media.Path,
        _ => Key,
    };

    /// <summary>A video playing in the mini player plays on there; anything else stops with its page.</summary>
    public override void Dispose()
    {
        if (Content is MediaPage media)
            media.PageClosed();
        else
            (Content as IDisposable)?.Dispose();
    }
}

/// <summary>
/// A picture: fitted to the page or at its own size, its size and the file's, the folder's other pictures one before or
/// after it.
/// </summary>
public sealed partial class ImagePage : ObservableObject, IDisposable
{
    public ImagePage(string path) => Load(path);

    [ObservableProperty]
    public partial string Path { get; private set; } = "";

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    public partial string Info { get; private set; } = "";

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Fitted to the page (else at its own size, scrolled).</summary>
    [ObservableProperty]
    public partial bool Fit { get; set; } = true;

    /// <summary>"3 of 12": where it is among the folder's pictures.</summary>
    [ObservableProperty]
    public partial string Place { get; private set; } = "";

    void Load(string path)
    {
        Image?.Dispose();
        Image = null;
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try
        {
            using var stream = File.OpenRead(path);
            Image = new Bitmap(stream);
            Info = $"{Image.PixelSize.Width} × {Image.PixelSize.Height} · {ResourceSampler.Bytes(new FileInfo(path).Length)}";
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Info = "";
            Problem = $"{Name} could not be shown: {ex.Message}";
        }
        var siblings = Siblings();
        Place = siblings.Count > 1 ? $"{siblings.IndexOf(path) + 1} of {siblings.Count}" : "";
    }

    List<string> Siblings()
    {
        try
        {
            return [.. Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(Path)!)
                .Where(f => Files.KindOf(f) == FileKind.Image)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [Path];
        }
    }

    [RelayCommand]
    void Next() => Step(1);

    [RelayCommand]
    void Previous() => Step(-1);

    void Step(int by)
    {
        var siblings = Siblings();
        if (siblings.Count < 2)
            return;
        var at = Math.Max(0, siblings.IndexOf(Path));
        Load(siblings[(at + by + siblings.Count) % siblings.Count]);
    }

    [RelayCommand]
    void ToggleFit() => Fit = !Fit;

    [RelayCommand]
    void OpenWithSystem() => Files.OpenWithSystem(Path);

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }
}

/// <summary>
/// A video or a sound played in Karvon by GStreamer (<see cref="GstPlayer"/>): played as it opens, paused, sought on its
/// line, its volume; a video's pictures drawn on the page. Where GStreamer is not, it says so and opens with the system's
/// player.
/// </summary>
public sealed partial class MediaPage : ObservableObject, IDisposable
{
    readonly GstPlayer? _player;
    readonly DispatcherTimer _timer;
    VideoFrame? _pending;
    bool _posted;
    bool _updating;

    /// <summary>Where the sound goes (the tests': fakesink); the speakers otherwise.</summary>
    internal static string? AudioSink { get; set; }

    public MediaPage(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsVideo = Files.KindOf(path) == FileKind.Video;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        if (!GstPlayer.IsAvailable)
        {
            Problem = "Karvon plays videos and sounds with GStreamer, which this computer does not have: open it with the system's player.";
            return;
        }
        try
        {
            _player = new GstPlayer(path, AudioSink);
            _player.Frame += OnFrame;
            _player.Ended += () => Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                Tick();
            });
            _player.Failed += text => Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                Problem = $"{Name} could not be played: {text}";
            });
            _player.Volume = Volume / 100;
            _timer.Start();
            Play();
        }
        catch (InvalidOperationException ex)
        {
            Problem = $"{Name} could not be played: {ex.Message}";
        }
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsVideo { get; }

    public bool IsSound => !IsVideo;

    public bool CanPlay => _player is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon), nameof(PlayTip))]
    public partial bool IsPlaying { get; private set; }

    public string PlayIcon => IsPlaying ? "Pause" : "Start";

    public string PlayTip => IsPlaying ? "Pause (Space)" : "Play (Space)";

    /// <summary>Where it is, in seconds: moved on the line, it seeks there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial double Position { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial double Duration { get; private set; }

    public string TimeText => $"{Clock(Position)} / {Clock(Duration)}";

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 80;

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>The video's picture now.</summary>
    [ObservableProperty]
    public partial WriteableBitmap? Frame { get; private set; }

    /// <summary>A new picture was drawn into <see cref="Frame"/>: its image is drawn again.</summary>
    public event Action? FrameChanged;

    /// <summary>It plays in the mini player (a small window over the others), its page open or not: shown there alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHere), nameof(ShowsInMini))]
    public partial bool IsMini { get; set; }

    /// <summary>Its pictures are on its page (not in the mini player).</summary>
    public bool ShowsHere => IsVideo && !IsMini;

    /// <summary>Its page says it plays in the mini player.</summary>
    public bool ShowsInMini => IsVideo && IsMini;

    /// <summary>Its page was closed while it played in the mini player: it stops when the mini player closes.</summary>
    public bool IsPageClosed { get; private set; }

    /// <summary>The mini player is asked for (the page's view opens its window).</summary>
    public event Action? MiniRequested;

    /// <summary>Its page shown again (opened again when it was closed).</summary>
    internal Action? Reveal { get; set; }

    [RelayCommand]
    void Mini() => MiniRequested?.Invoke();

    [RelayCommand]
    void ShowPage() => Reveal?.Invoke();

    /// <summary>Its page closes: it stops, unless the mini player plays it on.</summary>
    public void PageClosed()
    {
        if (IsMini)
            IsPageClosed = true;
        else
            Dispose();
    }

    /// <summary>Its page is open again.</summary>
    internal void PageOpened() => IsPageClosed = false;

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnPositionChanged(double value)
    {
        if (!_updating)
            _player?.Seek(TimeSpan.FromSeconds(value));
    }

    partial void OnVolumeChanged(double value)
    {
        if (_player is not null)
            _player.Volume = value / 100;
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (IsPlaying)
        {
            _player?.Pause();
            IsPlaying = false;
        }
        else
            Play();
    }

    void Play()
    {
        if (_player is null)
            return;
        // Played to its end: from the start again.
        if (Duration > 0 && Position >= Duration - 0.25)
            _player.Seek(TimeSpan.Zero);
        _player.Play();
        IsPlaying = true;
    }

    [RelayCommand]
    void OpenWithSystem()
    {
        if (IsPlaying)
            PlayPause();
        Files.OpenWithSystem(Path);
    }

    void Tick()
    {
        if (_player is null)
            return;
        _updating = true;
        Duration = _player.Duration.TotalSeconds;
        Position = Math.Min(Duration > 0 ? Duration : double.MaxValue, _player.Position.TotalSeconds);
        _updating = false;
    }

    /// <summary>A picture from the player's thread: the newest drawn on the window's, those it could not keep up with left.</summary>
    void OnFrame(VideoFrame frame)
    {
        _pending = frame;
        if (_posted)
            return;
        _posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _posted = false;
            if (_pending is { } latest)
                Draw(latest);
        }, DispatcherPriority.Render);
    }

    void Draw(VideoFrame frame)
    {
        if (Frame is null || Frame.PixelSize.Width != frame.Width || Frame.PixelSize.Height != frame.Height)
            Frame = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = Frame.Lock())
        {
            var row = frame.Width * 4;
            for (var y = 0; y < frame.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * row, buffer.Address + y * buffer.RowBytes, row);
        }
        FrameChanged?.Invoke();
    }

    static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _timer.Stop();
        _player?.Dispose();
        IsPlaying = false;
    }
}
