using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Http;

namespace Karvon.Postman.ViewModels;

/// <summary>
/// A request's document: its method and URL, its parameters, sign-in, headers, body and scripts, sent with the environment
/// chosen; what came back (the body, the headers, the tests, what scripts printed, the request as it went out).
/// </summary>
public sealed partial class PostmanRequestDocument : PostmanDocument
{
    /// <summary>The sign-ins that can be chosen, by their place; one Karvon does not send is the last, as it is.</summary>
    static readonly (string? Type, string Name)[] AuthChoices =
        [(null, "Inherit from parent"), (PostmanAuth.NoAuth, "No Auth"), (PostmanAuth.Bearer, "Bearer Token"), (PostmanAuth.Basic, "Basic Auth"), (PostmanAuth.ApiKey, "API Key"), (PostmanAuth.OAuth2, "OAuth 2.0 (access token)")];

    readonly List<PostmanResult> _history = [];
    PostmanCollection _collection;
    JsonObject _node;
    string _path;
    PostmanRequest _saved = new();
    PostmanAuth? _otherAuth;
    CancellationTokenSource? _sending;
    bool _loading, _syncing;

    public PostmanRequestDocument(IPostmanHost host, PostmanItem item) : base(host)
    {
        (_collection, _node, _path) = (item.Collection, item.Node, item.Collection.PathOf(item));
        Params.Edited += () =>
        {
            SyncUrl();
            Touched();
        };
        PathVariables.Edited += Touched;
        Headers.Edited += Touched;
        Form.Edited += Touched;
        FormData.Edited += Touched;
        Host.Library.Changed += OnLibraryChanged;
        Reload();
    }

    // ---- What it is of ----------------------------------------------------------------------------------------

    /// <summary>Its item as the collection has it now; null when it is gone.</summary>
    public PostmanItem? Item => _collection.Of(_node);

    public string File => _collection.File;

    public bool Is(PostmanItem item) => item.Node == _node || (item.Collection.File == File && item.Collection.PathOf(item) == _path);

    public override string Key => $"postman:{File}:{_path}";

    public string Name => Item?.Name ?? _path.Split('/')[^1];

    public override string Title => $"{Method} {Name}" + (IsDirty ? "*" : "");

    public override string ToolTip => $"{_collection.Name}/{_path}";

    /// <summary>Where it is in its collection, above the URL.</summary>
    public string Place => string.Join("  ›  ", _path.Split('/').Prepend(_collection.Name));

    protected override string SavedName => Name;

    /// <summary>The files were read again, or an item changed: this one is found again, and read when it has no changes of its own.</summary>
    void OnLibraryChanged()
    {
        if (Host.Library.Collections.FirstOrDefault(c => c.File == File) is { } collection)
        {
            _collection = collection;
            var item = collection.Of(_node) ?? collection.Find(_path);
            if (item is { IsFolder: false })
            {
                (_node, _path) = (item.Node, collection.PathOf(item));
                if (!IsDirty && !item.Read().Same(_saved))
                    Reload();
            }
        }
        Named();
        OnPropertyChanged(nameof(EnvironmentText));
        OnPropertyChanged(nameof(InheritedText));
    }

    void Named()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Place));
        OnPropertyChanged(nameof(ToolTip));
    }

    // ---- What it has ------------------------------------------------------------------------------------------

    public static IReadOnlyList<string> Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    [ObservableProperty]
    public partial string Method { get; set; } = "GET";

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    public PostmanRows Params { get; } = new();

    public PostmanRows PathVariables { get; } = new(fixedKeys: true);

    public bool HasPathVariables => PathVariables.Rows.Count > 0;

    public PostmanRows Headers { get; } = new();

    public ObservableCollection<string> AuthNames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherit), nameof(IsBearer), nameof(IsBasic), nameof(IsApiKey), nameof(IsOAuth), nameof(IsOtherAuth))]
    public partial int AuthIndex { get; set; }

    string? AuthType => AuthIndex >= 0 && AuthIndex < AuthChoices.Length ? AuthChoices[AuthIndex].Type : _otherAuth?.Type;

    public bool IsInherit => AuthIndex == 0;

    public bool IsBearer => AuthType == PostmanAuth.Bearer;

    public bool IsBasic => AuthType == PostmanAuth.Basic;

    public bool IsApiKey => AuthType == PostmanAuth.ApiKey;

    public bool IsOAuth => AuthType == PostmanAuth.OAuth2;

    public bool IsOtherAuth => AuthIndex >= AuthChoices.Length;

    /// <summary>Whose sign-in it is, when it has none of its own.</summary>
    public string InheritedText => Item?.Parent?.EffectiveAuth() is { } folder ? $"Its folder's or collection's: {folder.Type}."
        : PostmanAuth.Read(_collection.Root["auth"]) is { } own ? $"The collection's: {own.Type}." : "The collection has none: it is sent without.";

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ApiKeyName { get; set; } = "";

    [ObservableProperty]
    public partial string ApiKeyValue { get; set; } = "";

    public static IReadOnlyList<string> ApiKeyPlaces { get; } = ["Header", "Query Params"];

    [ObservableProperty]
    public partial int ApiKeyPlace { get; set; }

    [ObservableProperty]
    public partial string AccessToken { get; set; } = "";

    [ObservableProperty]
    public partial string HeaderPrefix { get; set; } = "Bearer";

    public static IReadOnlyList<string> BodyNames { get; } = ["none", "form-data", "x-www-form-urlencoded", "raw", "binary", "GraphQL"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoBody), nameof(IsFormData), nameof(IsUrlEncoded), nameof(IsRaw), nameof(IsBinary), nameof(IsGraphQl))]
    public partial int BodyIndex { get; set; }

    public string BodyMode => PostmanBody.Modes[Math.Clamp(BodyIndex, 0, PostmanBody.Modes.Count - 1)];

    public bool IsNoBody => BodyMode == PostmanBody.None;

    public bool IsFormData => BodyMode == PostmanBody.FormData;

    public bool IsUrlEncoded => BodyMode == PostmanBody.UrlEncoded;

    public bool IsRaw => BodyMode == PostmanBody.RawMode;

    public bool IsBinary => BodyMode == PostmanBody.FileMode;

    public bool IsGraphQl => BodyMode == PostmanBody.GraphQl;

    public PostmanRows Form { get; } = new();

    public PostmanRows FormData { get; } = new(canBeFiles: true);

    public static IReadOnlyList<string> Languages { get; } = ["json", "text", "xml", "html", "javascript"];

    [ObservableProperty]
    public partial string Language { get; set; } = "json";

    [ObservableProperty]
    public partial string Raw { get; set; } = "";

    [ObservableProperty]
    public partial string BodyFile { get; set; } = "";

    [ObservableProperty]
    public partial string GraphQuery { get; set; } = "";

    [ObservableProperty]
    public partial string GraphVariables { get; set; } = "";

    [ObservableProperty]
    public partial string PreRequest { get; set; } = "";

    [ObservableProperty]
    public partial string Tests { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    /// <summary>The editors' texts are to be read again (the request was).</summary>
    public event Action? Loaded;

    static readonly HashSet<string> Kept =
    [
        nameof(Method), nameof(Url), nameof(AuthIndex), nameof(Token), nameof(Username), nameof(Password), nameof(ApiKeyName), nameof(ApiKeyValue), nameof(ApiKeyPlace),
        nameof(AccessToken), nameof(HeaderPrefix), nameof(BodyIndex), nameof(Language), nameof(Raw), nameof(BodyFile), nameof(GraphQuery), nameof(GraphVariables),
        nameof(PreRequest), nameof(Tests), nameof(Description),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is not { } name || !Kept.Contains(name))
            return;
        if (name == nameof(Url))
            SyncParams();
        Touched();
    }

    void Touched()
    {
        if (_loading)
            return;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The URL typed: its query's rows follow (those that are off stay), and its path's names.</summary>
    void SyncParams()
    {
        if (_syncing)
            return;
        _syncing = true;
        var fresh = new Queue<PostmanPair>(PostmanRequest.QueryOf(Url));
        var rows = new List<PostmanPair>();
        foreach (var old in Params.Pairs)
            if (!old.Enabled)
                rows.Add(old);
            else if (fresh.TryDequeue(out var pair))
                rows.Add(pair with { Description = old.Key == pair.Key ? old.Description : "" });
        Params.Load([.. rows, .. fresh]);
        SyncPath();
        _syncing = false;
    }

    /// <summary>A row of the query changed: the URL follows.</summary>
    void SyncUrl()
    {
        if (_syncing || _loading)
            return;
        _syncing = true;
        Url = PostmanRequest.WithQuery(Url, Params.Pairs);
        _syncing = false;
    }

    void SyncPath()
    {
        var names = PostmanRequest.PathNames(Url);
        if (names.SequenceEqual(PathVariables.Rows.Select(r => r.Key)))
            return;
        var before = PathVariables.Pairs;
        PathVariables.Load(names.Select(n => before.FirstOrDefault(p => p.Key == n) ?? new PostmanPair(n, "")));
        OnPropertyChanged(nameof(HasPathVariables));
    }

    /// <summary>The request as its collection has it.</summary>
    public void Reload()
    {
        if (Item is not { } item)
            return;
        _loading = true;
        var request = item.Read();
        Method = request.Method;
        Url = request.Url;
        Params.Load(request.Query);
        var path = PostmanRequest.PathNames(request.Url);
        PathVariables.Load(path.Select(n => request.Path.FirstOrDefault(p => p.Key == n) ?? new PostmanPair(n, "")));
        OnPropertyChanged(nameof(HasPathVariables));
        Headers.Load(request.Headers);
        LoadAuth(request.Auth);
        BodyIndex = Math.Max(0, PostmanBody.Modes.ToList().IndexOf(request.Body.Mode));
        Raw = request.Body.Raw;
        Language = request.Body.Mode == PostmanBody.RawMode ? request.Body.Language : "json";
        Form.Load(request.Body.Mode == PostmanBody.UrlEncoded ? request.Body.Pairs : []);
        FormData.Load(request.Body.Mode == PostmanBody.FormData ? request.Body.Pairs : []);
        BodyFile = request.Body.File;
        GraphQuery = request.Body.Query;
        GraphVariables = request.Body.Variables;
        PreRequest = request.PreRequest;
        Tests = request.Tests;
        Description = request.Description;
        _loading = false;
        // As these rows and texts say it: what is compared with from now on.
        _saved = Current();
        Loaded?.Invoke();
        OnPropertyChanged(nameof(IsDirty));
        Named();
    }

    void LoadAuth(PostmanAuth? auth)
    {
        var index = Array.FindIndex(AuthChoices, c => c.Type == auth?.Type);
        _otherAuth = index < 0 ? auth : null;
        AuthNames.Clear();
        foreach (var (_, name) in AuthChoices)
            AuthNames.Add(name);
        if (_otherAuth is not null)
            AuthNames.Add($"{_otherAuth.Type} (not sent by Karvon)");
        AuthIndex = index < 0 ? AuthChoices.Length : index;
        Token = auth?.Type == PostmanAuth.Bearer ? auth["token"] : "";
        Username = auth?.Type == PostmanAuth.Basic ? auth["username"] : "";
        Password = auth?.Type == PostmanAuth.Basic ? auth["password"] : "";
        ApiKeyName = auth?.Type == PostmanAuth.ApiKey ? auth["key"] : "";
        ApiKeyValue = auth?.Type == PostmanAuth.ApiKey ? auth["value"] : "";
        ApiKeyPlace = auth?.Type == PostmanAuth.ApiKey && auth["in"] == "query" ? 1 : 0;
        AccessToken = auth?.Type == PostmanAuth.OAuth2 ? auth["accessToken"] : "";
        HeaderPrefix = auth?.Type == PostmanAuth.OAuth2 && auth.Values.TryGetValue("headerPrefix", out var prefix) ? prefix : "Bearer";
        _auth = auth;
    }

    /// <summary>The sign-in read: its values that are not shown stay.</summary>
    PostmanAuth? _auth;

    PostmanAuth? CurrentAuth()
    {
        if (IsOtherAuth)
            return _otherAuth;
        if (AuthType is not { } type)
            return null;
        var values = _auth?.Type == type ? new Dictionary<string, string>(_auth.Values) : [];
        switch (type)
        {
            case PostmanAuth.Bearer:
                values["token"] = Token;
                break;
            case PostmanAuth.Basic:
                (values["username"], values["password"]) = (Username, Password);
                break;
            case PostmanAuth.ApiKey:
                (values["key"], values["value"]) = (ApiKeyName, ApiKeyValue);
                if (ApiKeyPlace == 1 || values.ContainsKey("in"))
                    values["in"] = ApiKeyPlace == 1 ? "query" : "header";
                break;
            case PostmanAuth.OAuth2:
                values["accessToken"] = AccessToken;
                if (HeaderPrefix != "Bearer" || values.ContainsKey("headerPrefix"))
                    values["headerPrefix"] = HeaderPrefix;
                break;
        }
        return new PostmanAuth(type, values);
    }

    /// <summary>The request as it is here.</summary>
    public PostmanRequest Current() => new()
    {
        Method = Method.Trim().ToUpperInvariant(),
        Url = Url,
        Query = Params.Pairs,
        Path = PathVariables.Pairs,
        Headers = Headers.Pairs,
        Auth = CurrentAuth(),
        Body = new PostmanBody(BodyMode)
        {
            Raw = Raw,
            Language = Language,
            Pairs = IsFormData ? FormData.Pairs : Form.Pairs,
            File = BodyFile,
            Query = GraphQuery,
            Variables = GraphVariables,
        },
        PreRequest = PreRequest,
        Tests = Tests,
        Description = Description,
    };

    public override bool IsDirty => !Current().Same(_saved);

    /// <summary>Put in its collection, which is written to its file; one changed on disk meanwhile is read again first.</summary>
    public override bool Save()
    {
        if (!IsDirty)
            return false;
        try
        {
            if (_collection.IsStale)
                _collection.Reload();
            var item = Item ?? _collection.Find(_path) ?? Restore();
            (_node, _path) = (item.Node, _collection.PathOf(item));
            var current = Current();
            current.Write(item.Node);
            _saved = current;
            Host.Library.Save(_collection);
            OnPropertyChanged(nameof(IsDirty));
            Named();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PostmanException)
        {
            Host.Status($"{Name} was not saved: {ex.Message}");
            return false;
        }
    }

    /// <summary>It was removed from its collection (by another program): it is put back, at its end.</summary>
    PostmanItem Restore() => _collection.Add(null, _path.Split('/')[^1], isFolder: false);

    [RelayCommand]
    void SaveRequest() => Save();

    // ---- Sent -------------------------------------------------------------------------------------------------

    public string EnvironmentText => Host.Library.Active?.Name ?? "No environment";

    [ObservableProperty]
    public partial bool IsSending { get; private set; }

    [RelayCommand]
    public async Task SendAsync()
    {
        if (IsSending || Item is not { } item)
            return;
        IsSending = true;
        _sending = new CancellationTokenSource();
        try
        {
            var sender = new PostmanSender(Host.Library.Active, Host.Library.Globals) { Folder = Host.Workspace };
            Show(await sender.SendAsync(item, Current(), _sending.Token));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsSending = false;
            _sending = null;
        }
    }

    [RelayCommand]
    void Cancel() => _sending?.Cancel();

    /// <summary>What came back, here or from Claude's call.</summary>
    public void Show(PostmanResult result)
    {
        _history.Insert(0, result);
        if (_history.Count > 20)
            _history.RemoveAt(20);
        Result = result;
        if (result.Exchange.Error is null && result is { ScriptError: not null } or { Failed: > 0 })
            ResponseTab = "tests";
        else if (ResponseTab == "tests" && result.Tests.Count == 0)
            ResponseTab = "body";
        History.Clear();
        foreach (var earlier in _history)
            History.Add(earlier);
    }

    /// <summary>This session's results, the latest first.</summary>
    public ObservableCollection<PostmanResult> History { get; } = [];

    [RelayCommand]
    void ShowEarlier(PostmanResult? result)
    {
        if (result is not null)
            Result = result;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(StatusText), nameof(IsSuccess), nameof(IsFailure), nameof(Summary), nameof(BodyText), nameof(BodyPath), nameof(HeadersText),
        nameof(RequestText), nameof(ConsoleText), nameof(TestRows), nameof(TestsTitle), nameof(HasFailedTests), nameof(ScriptError), nameof(MissingText))]
    public partial PostmanResult? Result { get; set; }

    public bool HasResult => Result is not null;

    HttpExchange? Exchange => Result?.Exchange;

    public string StatusText => Exchange?.StatusText ?? "";

    public bool IsSuccess => Exchange?.Succeeded == true;

    public bool IsFailure => Exchange is { Succeeded: false };

    public string Summary => Exchange is { Error: null } e ? $"{e.Elapsed.TotalMilliseconds:0} ms · {Size(e.Size)}" + (e.ContentType is { } type ? $" · {type}" : "") : "";

    static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    public string BodyText => Exchange?.Error ?? Exchange?.Body ?? "";

    /// <summary>A name for the body's highlighting.</summary>
    public string BodyPath => Exchange?.ContentType switch
    {
        { } t when t.Contains("json", StringComparison.OrdinalIgnoreCase) => "response.json",
        { } t when t.Contains("html", StringComparison.OrdinalIgnoreCase) => "response.html",
        { } t when t.Contains("xml", StringComparison.OrdinalIgnoreCase) => "response.xml",
        _ => "response.txt",
    };

    public string HeadersText => Exchange is { } e ? string.Join('\n', e.Headers.Select(h => $"{h.Name}: {h.Value}")) : "";

    public string RequestText => Exchange is { } e
        ? $"{e.Method} {e.Url}\n" + string.Join('\n', e.RequestHeaders.Select(h => $"{h.Name}: {h.Value}")) + (e.RequestBody is { } b ? $"\n\n{b}" : "")
        : "";

    public string ConsoleText => Result is { Console.Count: > 0 } result ? string.Join('\n', result.Console) : "Nothing was printed.";

    public IReadOnlyList<PostmanTest> TestRows => Result?.Tests ?? [];

    public string TestsTitle => Result is { Tests.Count: > 0 } r ? $"Tests ({r.Passed}/{r.Tests.Count(t => !t.IsSkipped)})" : "Tests";

    public bool HasFailedTests => Result is { Failed: > 0 };

    public string? ScriptError => Result?.ScriptError is { } error ? $"A script stopped: {error}" : null;

    public string? MissingText => Result is { Missing.Count: > 0 } r && Exchange?.Error is null ? "Without a value, sent as written: " + string.Join(", ", r.Missing.Select(m => $"{{{{{m}}}}}")) : null;

    // ---- Tabs -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParams), nameof(IsAuth), nameof(IsHeaders), nameof(IsBody), nameof(IsPreRequest), nameof(IsTests), nameof(IsDocs))]
    public partial string Tab { get; set; } = "params";

    public bool IsParams => Tab == "params";

    public bool IsAuth => Tab == "auth";

    public bool IsHeaders => Tab == "headers";

    public bool IsBody => Tab == "body";

    public bool IsPreRequest => Tab == "prerequest";

    public bool IsTests => Tab == "tests";

    public bool IsDocs => Tab == "docs";

    [RelayCommand]
    void ShowTab(string? tab) => Tab = tab ?? "params";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResponseBody), nameof(IsResponseHeaders), nameof(IsResponseTests), nameof(IsResponseConsole), nameof(IsResponseRequest))]
    public partial string ResponseTab { get; set; } = "body";

    public bool IsResponseBody => ResponseTab == "body";

    public bool IsResponseHeaders => ResponseTab == "headers";

    public bool IsResponseTests => ResponseTab == "tests";

    public bool IsResponseConsole => ResponseTab == "console";

    public bool IsResponseRequest => ResponseTab == "request";

    [RelayCommand]
    void ShowResponseTab(string? tab) => ResponseTab = tab ?? "body";

    public override void Dispose()
    {
        _sending?.Cancel();
        Host.Library.Changed -= OnLibraryChanged;
    }
}
