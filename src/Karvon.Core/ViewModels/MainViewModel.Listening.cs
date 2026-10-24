using System.Text.RegularExpressions;

namespace Karvon.ViewModels;

/// <summary>Where the program Karvon runs listens: read from its output as it comes (app_control says it, Ports offers it).</summary>
public partial class MainViewModel
{
    readonly List<string> _listening = [];

    /// <summary>How far the output has been read (to the end of its last whole line).</summary>
    int _listeningRead;

    bool _wasRunning;

    public IReadOnlyList<string> ListeningUrls => IsRunning || Mode != DebugMode.Design ? _listening : [];

    public event Action? ListeningChanged;

    void WatchListening()
    {
        DebugOutput.Appended += ReadListening;
        DebugOutput.Cleared += () =>
        {
            _listeningRead = 0;
            if (_listening.Count == 0)
                return;
            _listening.Clear();
            ListeningChanged?.Invoke();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(IsRunning) or nameof(Mode)))
                return;
            var running = IsRunning || Mode != DebugMode.Design;
            if (running == _wasRunning)
                return;
            _wasRunning = running;
            ListeningChanged?.Invoke();
        };
    }

    /// <summary>
    /// The lines added since: ASP.NET Core's "Now listening on:" URLs; in a folder, a task's local URLs (Vite's
    /// "Local: http://localhost:5173/"...).
    /// </summary>
    void ReadListening()
    {
        var document = DebugOutput.Document;
        // Trimmed from the start (a long output): read again, what was said before stays.
        if (document.TextLength < _listeningRead)
            _listeningRead = 0;
        var text = document.GetText(_listeningRead, document.TextLength - _listeningRead);
        var end = text.LastIndexOf('\n');
        if (end < 0)
            return;
        _listeningRead += end + 1;
        var added = false;
        foreach (Match m in (IsFolderWorkspace ? LocalUrl() : ListeningLine()).Matches(text[..end]))
        {
            var url = m.Groups[1].Value.TrimEnd('/');
            if (_listening.Contains(url))
                continue;
            _listening.Add(url);
            added = true;
        }
        if (added)
            ListeningChanged?.Invoke();
    }

    [GeneratedRegex(@"Now listening on: (\S+)")]
    private static partial Regex ListeningLine();

    [GeneratedRegex(@"(https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\])(?::\d+)?[^\s\u001b'"")]*)")]
    private static partial Regex LocalUrl();
}
