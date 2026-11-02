using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Documents;

/// <summary>A page of a preview: its size, and its picture while it is in view.</summary>
public sealed partial class PreviewPage(int number, double width, double height) : ObservableObject
{
    public int Number => number;

    /// <summary>Its size at 100%, in device-independent pixels (a PDF point is 96/72 of one).</summary>
    public double Width { get; internal set; } = width;

    public double Height { get; internal set; } = height;

    [ObservableProperty]
    public partial double DisplayWidth { get; private set; } = width;

    [ObservableProperty]
    public partial double DisplayHeight { get; private set; } = height;

    /// <summary>Drawn while it is in view (or near it); null otherwise, so a long document takes little memory.</summary>
    [ObservableProperty]
    public partial Bitmap? Image { get; internal set; }

    internal bool Wanted { get; set; }

    internal void Scale(double zoom) => (DisplayWidth, DisplayHeight) = (Width * zoom, Height * zoom);
}

/// <summary>
/// A PDF, Word, Excel or PowerPoint file shown in a document of its own: its pages drawn by the document engines (a Word,
/// Excel or PowerPoint file as its own engine writes it as a PDF), each while it is in view, zoomed, and drawn again
/// when the file changes (Claude saved it, or another program did).
/// </summary>
public sealed partial class DocumentPreviewTab : DocumentTab
{
    /// <summary>The pictures' resolution: sharp at up to 150%.</summary>
    public const int Dpi = 144;

    /// <summary>The pages shown at most.</summary>
    public const int MaxPages = 2000;

    readonly string _path;
    readonly DocumentSessions _sessions;
    readonly Stack<PreviewPage> _toDraw = [];
    readonly FileSystemWatcher? _watcher;
    readonly DispatcherTimer _changed;
    CancellationTokenSource? _loading;
    OpenDocument? _pdf;
    string? _temporary;
    bool _drawing;
    bool _disposed;

    public DocumentPreviewTab(string path, DocumentSessions sessions)
    {
        _path = path;
        _sessions = sessions;
        Kind = DocumentKind.Of(path) ?? throw new ArgumentException($"{path} is no document the engines show.", nameof(path));
        // A change on disk is shown once it has settled (a save writes in steps).
        _changed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _changed.Tick += (_, _) =>
        {
            _changed.Stop();
            _ = LoadAsync();
        };
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Changed);
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(Changed);
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Changed);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }
        _ = LoadAsync();
    }

    public override string Key => _path;

    public override string Title => Path.GetFileName(_path);

    public DocumentKind Kind { get; }

    public string KindText => Kind.Name;

    public ObservableCollection<PreviewPage> Pages { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    /// <summary>The widest page as wide as the view (as it opens, until zoomed).</summary>
    [ObservableProperty]
    public partial bool FitWidth { get; set; } = true;

    /// <summary>The view's width, for <see cref="FitWidth"/> (the view sets it).</summary>
    public double ViewportWidth
    {
        get;
        set
        {
            field = value;
            Fit();
        }
    }

    public string ZoomText => $"{Math.Round(Zoom * 100)} %";

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        foreach (var page in Pages)
            page.Scale(value);
    }

    partial void OnFitWidthChanged(bool value) => Fit();

    void Fit()
    {
        // The pages' margins (16 at each side) and a vertical scroll bar's room.
        if (FitWidth && ViewportWidth > 64 && Pages.Count > 0)
            Zoom = Math.Clamp(Math.Round((ViewportWidth - 48) / Pages.Max(p => p.Width), 3), 0.25, 4);
    }

    [RelayCommand]
    void ZoomIn()
    {
        FitWidth = false;
        Zoom = Math.Min(4, Math.Round(Zoom * 1.25, 2));
    }

    [RelayCommand]
    void ZoomOut()
    {
        FitWidth = false;
        Zoom = Math.Max(0.25, Math.Round(Zoom / 1.25, 2));
    }

    [RelayCommand]
    void ActualSize()
    {
        FitWidth = false;
        Zoom = 1;
    }

    [RelayCommand]
    void FitToWidth() => FitWidth = true;

    [RelayCommand]
    Task ReloadAsync() => LoadAsync();

    /// <summary>The file in the program the system opens it with (LibreOffice, Word, a PDF reader).</summary>
    [RelayCommand]
    void OpenExternally() => Services.DesktopShell.Open(_path);

    void Changed()
    {
        if (_disposed)
            return;
        _changed.Stop();
        _changed.Start();
    }

    /// <summary>The file read again: a Word, Excel or PowerPoint file written as a PDF, the PDF opened, its pages' sizes known (each is drawn once it is in view).</summary>
    public async Task LoadAsync()
    {
        if (_disposed)
            return;
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var ct = cts.Token;
        IsLoading = true;
        Status = $"Opening {Title}...";
        string? temporary = null;
        OpenDocument? pdf = null;
        try
        {
            var file = _path;
            if (Kind != DocumentKind.Pdf)
            {
                temporary = Path.Combine(Path.GetTempPath(), $"karvon-preview-{Environment.ProcessId}-{Guid.NewGuid():N}.pdf");
                var (document, _) = await _sessions.OpenAsync(Kind, _path, null, ct);
                try
                {
                    var written = Kind == DocumentKind.Excel
                        ? await _sessions.CallAsync(document, "execute_command", new JsonObject
                        {
                            ["command"] = "file.exportPdf",
                            ["params"] = new JsonObject { ["path"] = temporary, ["sheets"] = "all" },
                        }, ct)
                        : await _sessions.SaveAsync(document, temporary, ct);
                    if (written.IsError)
                        throw new InvalidOperationException(written.Text);
                }
                finally
                {
                    _sessions.Close(document.Id);
                }
                file = temporary;
            }
            (pdf, _) = await _sessions.OpenAsync(DocumentKind.Pdf, file, null, ct);
            var info = await _sessions.CallAsync(pdf, "doc_info", [], ct);
            if (info.IsError)
                throw new InvalidOperationException(info.Text);
            var sizes = (JsonNode.Parse(info.Text)?["pages"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => (Width: p["width"]?.GetValue<double>() ?? 612, Height: p["height"]?.GetValue<double>() ?? 792)).ToList();
            ct.ThrowIfCancellationRequested();
            // The new pages in place of the old ones: what was drawn goes, what is in view is drawn again.
            Release();
            (_pdf, pdf) = (pdf, _pdf);
            (_temporary, temporary) = (temporary, _temporary);
            _toDraw.Clear();
            var pages = sizes.Take(MaxPages).Select((s, i) => new PreviewPage(i + 1, s.Width * 96 / 72, s.Height * 96 / 72)).ToList();
            foreach (var page in pages)
                page.Scale(Zoom);
            Pages.Clear();
            foreach (var page in pages)
                Pages.Add(page);
            Fit();
            Status = sizes.Count > MaxPages ? $"{sizes.Count} pages (the first {MaxPages} shown)" : sizes.Count == 1 ? "1 page" : $"{sizes.Count} pages";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            Status = $"{Title} could not be shown: {ex.Message}";
        }
        finally
        {
            if (pdf is not null)
                _sessions.Close(pdf.Id);
            Delete(temporary);
            if (_loading == cts)
                IsLoading = false;
        }
    }

    /// <summary>A page came into view: drawn (the latest first, as the view scrolls).</summary>
    public void Show(PreviewPage page)
    {
        page.Wanted = true;
        if (page.Image is not null)
            return;
        _toDraw.Push(page);
        if (!_drawing)
            _ = DrawAsync();
    }

    /// <summary>A page went out of view: its picture goes.</summary>
    public void Hide(PreviewPage page)
    {
        page.Wanted = false;
        var image = page.Image;
        page.Image = null;
        image?.Dispose();
    }

    async Task DrawAsync()
    {
        _drawing = true;
        try
        {
            while (!_disposed && _toDraw.TryPop(out var page))
            {
                if (!page.Wanted || page.Image is not null || _pdf is not { } pdf || !Pages.Contains(page))
                    continue;
                var drawn = await _sessions.CallAsync(pdf, "page_render", new JsonObject { ["page"] = page.Number, ["dpi"] = Dpi }, _loading?.Token ?? default);
                if (drawn.IsError || drawn.Images is not [var png, ..])
                {
                    Status = $"Page {page.Number} could not be drawn: {drawn.Text}";
                    continue;
                }
                using var stream = new MemoryStream(Convert.FromBase64String(png));
                var image = new Bitmap(stream);
                if (!page.Wanted || !Pages.Contains(page) || _disposed)
                {
                    image.Dispose();
                    continue;
                }
                // The size it is drawn at (a page turned is wider than its media box says).
                var width = page.Width;
                (page.Width, page.Height) = (image.PixelSize.Width * 96.0 / Dpi, image.PixelSize.Height * 96.0 / Dpi);
                page.Scale(Zoom);
                page.Image = image;
                if (Math.Abs(width - page.Width) > 1)
                    Fit();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or FormatException or ObjectDisposedException)
        {
        }
        finally
        {
            _drawing = false;
        }
    }

    void Release()
    {
        foreach (var page in Pages)
        {
            page.Image?.Dispose();
            page.Image = null;
        }
        if (_pdf is { } pdf)
            _sessions.Close(pdf.Id);
        _pdf = null;
        Delete(_temporary);
        _temporary = null;
    }

    static void Delete(string? file)
    {
        if (file is null)
            return;
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        _loading?.Cancel();
        _changed.Stop();
        _watcher?.Dispose();
        Release();
        base.Dispose();
    }
}
