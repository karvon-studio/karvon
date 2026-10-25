using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Editor;

namespace Karvon.Http.ViewModels;

/// <summary>An environment of the file's choice: one of http-client.env.json's, or none.</summary>
public sealed record HttpEnvironmentChoice(string? Name)
{
    public override string ToString() => Name ?? "No Environment";
}

/// <summary>
/// The response pane beside a .http file: its environment (http-client.env.json's), the request at the caret (or a ▶
/// clicked in the margin) sent, what came back (status, time, size, body, headers, the request as sent), and the
/// file's earlier responses this session. A named request's response gives the later ones its values.
/// </summary>
public sealed partial class HttpPaneViewModel : ObservableObject
{
    readonly EditorHost _host;
    readonly Func<string?> _root;
    readonly Dictionary<string, HttpExchange> _named = new(StringComparer.Ordinal);
    DispatcherTimer? _parse;
    CancellationTokenSource? _sending;

    public HttpPaneViewModel(EditorHost host, Func<string?> root, string? environment)
    {
        _host = host;
        _root = root;
        Reparse();
        LoadEnvironments(environment);
        host.Document.Changed += (_, _) => ScheduleParse();
    }

    public string FilePath => _host.FilePath;

    public IReadOnlyList<HttpRequestBlock> Requests { get; private set; } = [];

    IReadOnlyList<HttpVariable> _variables = [];

    /// <summary>The requests' lines changed: the margin redraws.</summary>
    public event Action? RequestsChanged;

    public ObservableCollection<HttpEnvironmentChoice> Environments { get; } = [];

    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _environments = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    [ObservableProperty]
    public partial HttpEnvironmentChoice? Environment { get; set; }

    /// <summary>The environment chosen changed (the module keeps it per file).</summary>
    public event Action<string?>? EnvironmentChosen;

    partial void OnEnvironmentChanged(HttpEnvironmentChoice? value) => EnvironmentChosen?.Invoke(value?.Name);

    public bool HasEnvironments => Environments.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExchange), nameof(StatusText), nameof(IsSuccess), nameof(IsFailure), nameof(Summary), nameof(HeadersText), nameof(RequestText),
        nameof(BodyPath))]
    public partial HttpExchange? Exchange { get; set; }

    public bool HasExchange => Exchange is not null;

    public string StatusText => Exchange?.StatusText ?? "";

    public bool IsSuccess => Exchange?.Succeeded == true;

    public bool IsFailure => Exchange is { Succeeded: false };

    public string Summary => Exchange is { Error: null } e ? $"{e.Elapsed.TotalMilliseconds:0} ms · {Size(e.Size)}" + (e.ContentType is { } t ? $" · {t}" : "") : "";

    public string HeadersText => Exchange is { } e ? string.Join('\n', e.Headers.Select(h => $"{h.Name}: {h.Value}")) : "";

    /// <summary>The request as it went out, its variables put in.</summary>
    public string RequestText => Exchange is { } e
        ? $"{e.Method} {e.Url}\n" + string.Join('\n', e.RequestHeaders.Select(h => $"{h.Name}: {h.Value}")) + (e.RequestBody is { } b ? $"\n\n{b}" : "")
        : "";

    /// <summary>A name for the body's highlighting (response.json, response.xml).</summary>
    public string BodyPath => Exchange?.ContentType switch
    {
        { } t when t.Contains("json", StringComparison.OrdinalIgnoreCase) => "response.json",
        { } t when t.Contains("html", StringComparison.OrdinalIgnoreCase) => "response.html",
        { } t when t.Contains("xml", StringComparison.OrdinalIgnoreCase) => "response.xml",
        _ => "response.txt",
    };

    static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBody), nameof(IsHeaders), nameof(IsRequest))]
    public partial string Tab { get; set; } = "body";

    public bool IsBody => Tab == "body";

    public bool IsHeaders => Tab == "headers";

    public bool IsRequest => Tab == "request";

    [RelayCommand]
    void ShowTab(string? tab) => Tab = tab ?? "body";

    [ObservableProperty]
    public partial bool IsSending { get; private set; }

    /// <summary>This session's responses of the file, the latest first.</summary>
    public ObservableCollection<HttpExchange> History { get; } = [];

    [ObservableProperty]
    public partial string? Hint { get; private set; } = "Put the caret in a request and Send it (or click ▶ beside it).";

    void ScheduleParse()
    {
        _parse ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _parse!.Stop();
            Reparse();
        });
        _parse.Stop();
        _parse.Start();
    }

    void Reparse()
    {
        (Requests, _variables) = HttpFile.Parse(_host.Document.Text);
        RequestsChanged?.Invoke();
    }

    [RelayCommand]
    public void LoadEnvironments(string? keep = null)
    {
        keep ??= Environment?.Name;
        _environments = HttpEnvironments.Load(FilePath, _root());
        Environments.Clear();
        Environments.Add(new HttpEnvironmentChoice(null));
        foreach (var name in _environments.Keys.Order())
            Environments.Add(new HttpEnvironmentChoice(name));
        Environment = (keep is null ? null : Environments.FirstOrDefault(e => e.Name == keep))
            ?? Environments.FirstOrDefault(e => e.Name is "dev" or "development" or "local") ?? Environments[0];
        OnPropertyChanged(nameof(HasEnvironments));
    }

    public HttpVariables Variables() => new(_variables,
        Environment?.Name is { } env && _environments.TryGetValue(env, out var values) ? values : new Dictionary<string, string>(),
        _named, Path.GetDirectoryName(FilePath));

    /// <summary>The request at the caret sent.</summary>
    [RelayCommand]
    Task SendCurrentAsync() => SendAtAsync(_host.CaretPosition.Line);

    /// <summary>The request a line is in sent; what came back shown.</summary>
    public async Task<HttpExchange?> SendAtAsync(int line)
    {
        Reparse();
        if (HttpFile.At(Requests, line) is not { } block)
        {
            Hint = "There is no request at the caret: write one (GET https://...), then send it.";
            return null;
        }
        return await SendAsync(block);
    }

    public async Task<HttpExchange> SendAsync(HttpRequestBlock block, CancellationToken ct = default)
    {
        _sending?.Cancel();
        _sending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsSending = true;
        Hint = null;
        try
        {
            var exchange = await HttpRunner.SendAsync(block, Variables(), _sending.Token);
            if (block.Name is { } name && exchange.Error is null)
                _named[name] = exchange;
            Exchange = exchange;
            History.Insert(0, exchange);
            while (History.Count > 20)
                History.RemoveAt(History.Count - 1);
            return exchange;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    void Cancel() => _sending?.Cancel();

    [RelayCommand]
    void ShowFromHistory(HttpExchange? exchange)
    {
        if (exchange is not null)
            Exchange = exchange;
    }
}
