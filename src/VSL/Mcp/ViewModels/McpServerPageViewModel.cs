using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelContextProtocol.Protocol;
using VSL.ViewModels;

namespace VSL.Mcp.ViewModels;

/// <summary>A tool of the server: what it says it does, a form for its arguments, and the last result.</summary>
public sealed partial class McpToolItem(McpServerPageViewModel page, Tool tool) : ObservableObject
{
    CancellationTokenSource? _running;

    public Tool Tool { get; } = tool;

    public string Name => Tool.Name;

    public string Description => Tool.Description ?? "";

    /// <summary>"The server says it reads only, may destroy data, reaches outside." (its hints, which it may not keep).</summary>
    public string? Hints { get; } = HintsOf(tool.Annotations);

    public McpForm Form { get; } = McpForm.ForTool(tool.InputSchema);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; set; }

    public bool HasResult => Result is not null;

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; set; }

    static string? HintsOf(ToolAnnotations? hints)
    {
        if (hints is null)
            return null;
        var said = new[]
        {
            hints.ReadOnlyHint == true ? "reads only" : null,
            hints.DestructiveHint == true && hints.ReadOnlyHint != true ? "may destroy data" : null,
            hints.IdempotentHint == true ? "can be called again safely" : null,
            hints.OpenWorldHint == true ? "reaches outside" : null,
        }.OfType<string>().ToList();
        return said.Count == 0 ? null : $"The server says it {string.Join(", ", said)}.";
    }

    /// <summary>Calls the tool with the form's values; a wrong value is shown instead, and the tool is not called.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task Run()
    {
        Dictionary<string, object?> arguments;
        try
        {
            arguments = Form.Values();
        }
        catch (FormatException ex)
        {
            (Result, IsError) = (ex.Message, true);
            return;
        }
        using var running = _running = new CancellationTokenSource();
        IsRunning = true;
        Result = "Running…";
        IsError = false;
        try
        {
            var reply = await page.EnsureConnectedAsync() is { } problem ? problem : await page.Connection.CallToolAsync(Name, arguments, running.Token);
            (Result, IsError) = (reply.Text, reply.IsError);
        }
        catch (OperationCanceledException)
        {
            (Result, IsError) = ("Cancelled.", true);
        }
        finally
        {
            _running = null;
            IsRunning = false;
        }
    }

    bool CanRun() => !IsRunning;

    [RelayCommand]
    void Cancel() => _running?.Cancel();
}

/// <summary>A resource of the server, and what reading it gave.</summary>
public sealed partial class McpResourceItem(McpServerPageViewModel page, Resource resource) : ObservableObject
{
    public Resource Resource { get; } = resource;

    public string Name => Resource.Name;

    public string Uri => Resource.Uri;

    public string Description => Resource.Description ?? Resource.MimeType ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    public partial string? Content { get; set; }

    public bool HasContent => Content is not null;

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadCommand))]
    public partial bool IsReading { get; set; }

    [RelayCommand(CanExecute = nameof(CanRead))]
    public async Task Read()
    {
        IsReading = true;
        Content = "Reading…";
        try
        {
            var reply = await page.EnsureConnectedAsync() ?? await page.Connection.ReadResourceAsync(Uri);
            (Content, IsError) = (reply.Text, reply.IsError);
        }
        finally
        {
            IsReading = false;
        }
    }

    bool CanRead() => !IsReading;
}

/// <summary>A prompt of the server: a form for its arguments, and the messages it gave.</summary>
public sealed partial class McpPromptItem(McpServerPageViewModel page, Prompt prompt) : ObservableObject
{
    public Prompt Prompt { get; } = prompt;

    public string Name => Prompt.Name;

    public string Description => Prompt.Description ?? "";

    public McpForm Form { get; } = McpForm.ForPrompt(prompt.Arguments);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessages))]
    public partial string? Messages { get; set; }

    public bool HasMessages => Messages is not null;

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetCommand))]
    public partial bool IsGetting { get; set; }

    [RelayCommand(CanExecute = nameof(CanGet))]
    public async Task Get()
    {
        Dictionary<string, object?> arguments;
        try
        {
            arguments = Form.Values();
        }
        catch (FormatException ex)
        {
            (Messages, IsError) = (ex.Message, true);
            return;
        }
        IsGetting = true;
        Messages = "Asking…";
        try
        {
            var reply = await page.EnsureConnectedAsync() ?? await page.Connection.GetPromptAsync(Name, arguments);
            (Messages, IsError) = (reply.Text, reply.IsError);
        }
        finally
        {
            IsGetting = false;
        }
    }

    bool CanGet() => !IsGetting;
}

/// <summary>
/// A server's page: who it is and how VSL reaches it, whether the Claude Code sessions VSL starts get it, then its
/// Tools (run with a form), Resources (read), Prompts (got) and the Log of everything said with it.
/// </summary>
public sealed partial class McpServerPageViewModel : ObservableObject, IDisposable
{
    readonly McpExplorerViewModel _explorer;

    public McpServerPageViewModel(McpExplorerViewModel explorer, McpServerNode node)
    {
        _explorer = explorer;
        Node = node;
        if (node.Connection.LogText is { Length: > 0 } log)
            Log.Append(log);
        node.Connection.Logged += OnLogged;
        node.PropertyChanged += OnNodeChanged;
        Rebuild();
    }

    public McpServerNode Node { get; }

    internal McpConnection Connection => Node.Connection;

    public OutputChannel Log { get; } = new("MCP");

    public ObservableCollection<McpToolItem> Tools { get; } = [];

    public ObservableCollection<McpResourceItem> Resources { get; } = [];

    public ObservableCollection<McpPromptItem> Prompts { get; } = [];

    [ObservableProperty]
    public partial McpToolItem? SelectedTool { get; set; }

    [ObservableProperty]
    public partial McpResourceItem? SelectedResource { get; set; }

    [ObservableProperty]
    public partial McpPromptItem? SelectedPrompt { get; set; }

    /// <summary>"tools", "resources", "prompts" or "log".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTools), nameof(IsResources), nameof(IsPrompts), nameof(IsLog), nameof(ShowsWaiting), nameof(ShowsNothing))]
    public partial string Tab { get; set; } = "tools";

    public bool IsTools => Tab == "tools";

    public bool IsResources => Tab == "resources";

    public bool IsPrompts => Tab == "prompts";

    public bool IsLog => Tab == "log";

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    /// <summary>The transport line: "stdio npx -y server · server 1.2.0".</summary>
    public string TransportLine => Node.Transport + (Connection.ServerInfo is { Length: > 0 } info ? "  ·  " + info : "");

    public bool HasNothing => Connection.IsConnected && Tools.Count + Resources.Count + Prompts.Count == 0;

    /// <summary>Not connected (or failed): what to say where the lists would be.</summary>
    public string? Waiting => Connection.Status switch
    {
        McpStatus.Connected => null,
        McpStatus.Connecting => "Connecting…",
        McpStatus.NotConnected => "Not connected.",
        _ => Connection.StatusText,
    };

    public bool IsWaiting => Waiting is not null;

    /// <summary>Over the lists (the log shows what was said, connected or not).</summary>
    public bool ShowsWaiting => IsWaiting && !IsLog;

    public bool ShowsNothing => HasNothing && !IsLog;

    public bool ClaudeReadsItself => McpConfigs.ClaudeReadsItself(Node.Definition.Origin);

    public string ForClaudeTip => ClaudeReadsItself
        ? "Claude Code reads this server from its own configuration (it asks once before it starts those of .mcp.json)."
        : "The Claude Code sessions VSL starts get this server (from the next session).";

    /// <summary>Whether the Claude Code sessions VSL starts get it (always so for those Claude Code reads itself).</summary>
    public bool ForClaude
    {
        get => _explorer.Configs.IsForClaude(Node.Definition);
        set
        {
            if (ClaudeReadsItself || value == ForClaude)
                return;
            _explorer.SetForClaude(Node, value);
            OnPropertyChanged();
        }
    }

    public bool CanSwitchForClaude => !ClaudeReadsItself;

    /// <summary>Chooses a tool, a resource or a prompt (from the tree), on its tab.</summary>
    public void Select(string? kind, string? name)
    {
        switch (kind)
        {
            case "tool":
                Tab = "tools";
                SelectedTool = Tools.FirstOrDefault(t => t.Name == name) ?? SelectedTool;
                break;
            case "resource":
                Tab = "resources";
                SelectedResource = Resources.FirstOrDefault(r => r.Name == name) ?? SelectedResource;
                break;
            case "prompt":
                Tab = "prompts";
                SelectedPrompt = Prompts.FirstOrDefault(p => p.Name == name) ?? SelectedPrompt;
                break;
        }
        // Chosen before the server has said what it offers: chosen again once it has.
        _pending = kind is null || Connection.IsConnected ? null : (kind, name);
    }

    (string Kind, string? Name)? _pending;

    /// <summary>Connects first when it is not; a reply saying why when it cannot.</summary>
    internal async Task<McpReply?> EnsureConnectedAsync()
    {
        if (!Connection.IsConnected)
            await _explorer.ConnectAsync(Node);
        return Connection.IsConnected ? null : new McpReply($"{Node.Name} is {Connection.StatusText}.", true);
    }

    void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpServerNode.StatusText) or nameof(McpServerNode.Definition))
            Rebuild();
    }

    /// <summary>The lists, again from what the server offers (a tool keeps its form and result when it is still there).</summary>
    void Rebuild()
    {
        ContainersPageSync(Tools, Connection.Tools, t => t.Name, t => new McpToolItem(this, t), i => i.Name);
        ContainersPageSync(Resources, Connection.Resources, r => r.Uri, r => new McpResourceItem(this, r), i => i.Uri);
        ContainersPageSync(Prompts, Connection.Prompts, p => p.Name, p => new McpPromptItem(this, p), i => i.Name);
        SelectedTool = Tools.Contains(SelectedTool!) ? SelectedTool : Tools.FirstOrDefault();
        SelectedResource = Resources.Contains(SelectedResource!) ? SelectedResource : Resources.FirstOrDefault();
        SelectedPrompt = Prompts.Contains(SelectedPrompt!) ? SelectedPrompt : Prompts.FirstOrDefault();
        if (_pending is { } pending && Connection.IsConnected)
            Select(pending.Kind, pending.Name);
        foreach (var name in new[] { nameof(TransportLine), nameof(HasNothing), nameof(Waiting), nameof(IsWaiting), nameof(ShowsWaiting), nameof(ShowsNothing), nameof(ForClaude), nameof(ClaudeReadsItself), nameof(CanSwitchForClaude), nameof(ForClaudeTip) })
            OnPropertyChanged(name);
    }

    static void ContainersPageSync<TItem, TSource>(ObservableCollection<TItem> items, IReadOnlyList<TSource> source, Func<TSource, string> keyOf, Func<TSource, TItem> make, Func<TItem, string> itemKey)
        where TItem : class
    {
        var kept = source.Select(s => items.FirstOrDefault(i => itemKey(i) == keyOf(s)) ?? make(s)).ToList();
        Docker.ViewModels.ContainersPage.Sync(items, kept);
    }

    void OnLogged(string line) => Log.Append(line);

    [RelayCommand]
    Task ConnectAgain() => _explorer.ConnectAsync(Node);

    [RelayCommand]
    Task SignIn() => Node.Connection.SignInAsync();

    [RelayCommand]
    Task Edit() => _explorer.EditAsync(Node);

    [RelayCommand]
    void AskClaude() => _explorer.AskClaude(Node);

    public void Dispose()
    {
        Node.Connection.Logged -= OnLogged;
        Node.PropertyChanged -= OnNodeChanged;
    }
}
