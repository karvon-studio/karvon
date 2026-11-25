using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Postman.ViewModels;

/// <summary>
/// A collection's run (or a folder's): its requests one after another, with the environment chosen, as many times as asked,
/// each with its status and tests.
/// </summary>
public sealed partial class PostmanRunDocument : PostmanDocument
{
    PostmanCollection _collection;
    CancellationTokenSource? _running;

    public PostmanRunDocument(IPostmanHost host, PostmanCollection collection, PostmanItem? folder) : base(host)
    {
        _collection = collection;
        Folder = folder?.Path;
        host.Library.Changed += OnLibraryChanged;
    }

    public string File => _collection.File;

    /// <summary>The folder's path; null for the whole collection.</summary>
    public string? Folder { get; }

    public string Name => Folder is null ? _collection.Name : $"{_collection.Name}/{Folder}";

    public override string Key => $"postman-run:{File}:{Folder}";

    public override string Title => "Run " + (Folder?.Split('/')[^1] ?? _collection.Name);

    public override string ToolTip => Name;

    protected override string SavedName => Name;

    void OnLibraryChanged()
    {
        if (Host.Library.Collections.FirstOrDefault(c => c.File == File) is { } collection)
            _collection = collection;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(EnvironmentText));
        OnPropertyChanged(nameof(CountText));
    }

    IReadOnlyList<PostmanItem> Requests => [.. Folder is null ? _collection.Requests : _collection.Find(Folder)?.Requests ?? []];

    public string CountText => Requests.Count is var n && n == 1 ? "1 request" : $"{n} requests";

    public string EnvironmentText => Host.Library.Active?.Name ?? "No environment";

    [ObservableProperty]
    public partial int Iterations { get; set; } = 1;

    [ObservableProperty]
    public partial int Delay { get; set; }

    /// <summary>A CSV or JSON file whose rows the iterations are sent with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData), nameof(DataText))]
    public partial string? DataFile { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataText))]
    public partial string? DataProblem { get; private set; }

    public bool HasData => DataFile is not null;

    int _rows;

    public string DataText => DataProblem ?? (DataFile is null ? "" : $"{Path.GetFileName(DataFile)} ({_rows} {(_rows == 1 ? "row" : "rows")})");

    [RelayCommand]
    public async Task ChooseDataAsync()
    {
        if (await Host.PickFilesAsync("Data File (CSV or JSON)") is [var file, ..])
            UseData(file);
    }

    public void UseData(string file)
    {
        try
        {
            _rows = PostmanData.Read(file).Count;
            (DataFile, DataProblem, Iterations) = (file, null, _rows);
        }
        catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
        {
            (DataFile, DataProblem) = (null, ex.Message);
        }
    }

    [RelayCommand]
    void ForgetData() => (DataFile, DataProblem, Iterations) = (null, null, 1);

    [ObservableProperty]
    public partial bool StopOnFailure { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    public ObservableCollection<PostmanResult> Results { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasFailed { get; private set; }

    /// <summary>The result whose body and tests show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(SelectedText))]
    public partial PostmanResult? Selected { get; set; }

    public bool HasSelected => Selected is not null;

    public string SelectedText => Selected is { } r
        ? $"{r.Exchange.Method} {r.Exchange.Url}\n{r.Exchange.StatusText}\n"
          + string.Concat(r.Tests.Select(t => $"{(t.IsSkipped ? "-" : t.Passed ? "✓" : "✗")} {t.Name}{(t.Error is { } e ? ": " + e : "")}\n"))
          + (r.ScriptError is { } s ? $"A script stopped: {s}\n" : "") + (r.Console.Count > 0 ? "\n" + string.Join('\n', r.Console) + "\n" : "")
          + (r.Exchange.Body.Length > 0 ? "\n" + r.Exchange.Body : "")
        : "";

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning)
            return;
        IsRunning = true;
        Clear();
        _running = new CancellationTokenSource();
        try
        {
            var sender = Host.Sender();
            var data = DataFile is { } file ? await Task.Run(() => PostmanData.Read(file)) : [];
            var options = new PostmanRunOptions { Data = data, Iterations = Math.Clamp(Iterations, 1, 1000), Delay = TimeSpan.FromMilliseconds(Math.Clamp(Delay, 0, 600_000)), StopOnFailure = StopOnFailure };
            await sender.RunAsync(Requests, options, Add, _running.Token);
        }
        catch (OperationCanceledException)
        {
            Summary += " · stopped";
        }
        catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
        {
            DataProblem = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _running = null;
        }
    }

    [RelayCommand]
    void Stop() => _running?.Cancel();

    [RelayCommand]
    void Open(PostmanResult? result)
    {
        if (result is not null && _collection.Find(result.Path) is { } item)
            Host.Open(item)?.Show(result);
    }

    void Clear()
    {
        Results.Clear();
        Selected = null;
        Summary = "";
        HasFailed = false;
    }

    void Add(PostmanResult result)
    {
        Results.Add(result);
        Summary = Summarize(Results);
        HasFailed |= !result.Succeeded;
    }

    /// <summary>A run made elsewhere (Claude's).</summary>
    public void Show(IReadOnlyList<PostmanResult> results)
    {
        if (IsRunning)
            return;
        Clear();
        foreach (var result in results)
            Add(result);
    }

    /// <summary>A run in a line: its requests, its tests, its time.</summary>
    public static string Summarize(IReadOnlyCollection<PostmanResult> results)
    {
        var failed = results.Count(r => !r.Succeeded);
        var tests = results.Sum(r => r.Tests.Count(t => !t.IsSkipped));
        return $"{results.Count} sent" + (failed > 0 ? $", {failed} failed" : "") + (tests > 0 ? $" · {results.Sum(r => r.Passed)}/{tests} tests passed" : "")
            + $" · {results.Sum(r => r.Exchange.Elapsed.TotalMilliseconds):0} ms";
    }

    public override void Dispose()
    {
        _running?.Cancel();
        Host.Library.Changed -= OnLibraryChanged;
    }
}
