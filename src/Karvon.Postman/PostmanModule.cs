using System.Text.Json;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Modules;
using Karvon.Postman.ViewModels;
using Karvon.Postman.Views;
using Karvon.Services;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Postman;

/// <summary>
/// Postman: its collections and environments, from their JSON files (the open folder's, and those opened), in a tool window
/// (see <see cref="PostmanExplorerViewModel"/>); a request, an environment and a run open as documents. A request is sent
/// with its scripts and tests, as Postman sends it, without Postman or an account. Claude Code gets the Postman tools.
/// </summary>
public sealed partial class PostmanModule : KarvonModule, IPostmanHost
{
    public const string ToolWindowId = "Postman";

    const string ActiveKey = "postman.environment";

    PostmanLibrary? _library;

    public override string Id => "postman";

    public override string Name => "Postman";

    public override string Icon => "folder-postman";

    public override string Description =>
        "Postman's collections and environments, from their JSON files: requests edited and sent with their variables, sign-in, "
        + "scripts and tests, a collection or a folder run. An environment's values stay in Karvon's vault, and no agent gets "
        + "them. Claude Code's Postman tools. No account, and Postman need not be installed.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Postman", "_Postman", "ToolPostman", DockSide.Right, () => new PostmanExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
    [
        Document<PostmanRequestDocument>(document => new PostmanRequestView { DataContext = document }),
        Document<PostmanEnvironmentDocument>(document => new PostmanEnvironmentView { DataContext = document }),
        Document<PostmanRunDocument>(document => new PostmanRunView { DataContext = document }),
        Document<PostmanCookiesDocument>(document => new PostmanCookiesView { DataContext = document }),
    ];

    public override IDataTemplate OverlayTemplate => Template<PostmanExplorerViewModel>(_ => new PostmanOverlay());

    /// <summary>The explorer, once it has been needed (no file is read before).</summary>
    [ObservableProperty]
    public partial PostmanExplorerViewModel? Explorer { get; private set; }

    /// <summary>Where environments' values, cookies and access tokens are (the tests' own).</summary>
    internal SealedStore Store { get; set; } = SealedStore.Karvons;

    /// <summary>Where the list of the files opened is (the tests' own).</summary>
    internal string ListFile { get; set; } = Path.Combine(Services.Settings.ConfigDirectory, "postman.json");

    public PostmanLibrary Library => _library ??= NewLibrary();

    PostmanLibrary NewLibrary()
    {
        if (Store.Vault is { } vault)
            vault.Changed += OnVaultChanged;
        return new PostmanLibrary(() => Workbench.Workspace, ListFile, Store) { ActiveId = Workbench.WorkspaceState.GetValueOrDefault(ActiveKey) };
    }

    void OnVaultChanged() => Dispatcher.UIThread.Post(() =>
    {
        _library?.Reopen();
        _cookies?.Reopen();
    });

    PostmanCookies? _cookies;

    /// <summary>The user's cookies.</summary>
    public PostmanCookies Cookies => _cookies ??= new PostmanCookies(Store);

    PostmanOAuth? _oauth;

    public PostmanOAuth OAuth => _oauth ??= new PostmanOAuth(Store, OpenBrowser ?? (uri => Workbench.OpenUrl(uri.AbsoluteUri)), (title, label) => EnsureExplorer().AskAsync(title, label));

    /// <summary>Where a sign-in's page opens (the tests' own).</summary>
    internal Action<Uri>? OpenBrowser { get; set; }

    /// <summary>What agents set this session, theirs alone.</summary>
    internal PostmanAgentLayer Agents { get; } = new();

    public PostmanExplorerViewModel EnsureExplorer()
    {
        if (Explorer is null)
        {
            Overlay = Explorer = new PostmanExplorerViewModel(this);
            _ = Library.RefreshAsync();
        }
        return Explorer;
    }

    protected override void Started()
    {
        Workbench.FileSaved += OnFileSaved;
        Workbench.GitChanged += OnFilesChanged;
        new PostmanLibrary(() => null, ListFile, Store).SealOpened();
    }

    void OnFileSaved(string path)
    {
        if (PostmanLibrary.IsPostmanFile(path))
            OnFilesChanged();
    }

    /// <summary>Files may have changed (a checkout, the editor): what is shown follows.</summary>
    void OnFilesChanged()
    {
        if (_library is not null)
            _ = _library.RefreshAsync();
    }

    public override Task WorkspaceChangedAsync()
    {
        if (_library is not null)
        {
            _library.ActiveId = Workbench.WorkspaceState.GetValueOrDefault(ActiveKey);
            _ = _library.RefreshAsync();
        }
        return Task.CompletedTask;
    }

    public override void SaveWorkspaceState(IDictionary<string, string> state)
    {
        if (_library?.ActiveId is { } id)
            state[ActiveKey] = id;
        else if (_library is null && Workbench.WorkspaceState.GetValueOrDefault(ActiveKey) is { } kept)
            state[ActiveKey] = kept;
    }

    /// <summary>The solution closes: its requests and environments close with it, those changed asking first.</summary>
    public override async Task<bool> CloseWorkspaceAsync()
    {
        foreach (var document in Workbench.Documents.OfType<PostmanDocument>().ToList())
            if (!await document.CloseAsync())
                return false;
        return true;
    }

    public override void Dispose()
    {
        if (IsStarted)
        {
            Workbench.FileSaved -= OnFileSaved;
            Workbench.GitChanged -= OnFilesChanged;
        }
        if (_library is not null && Store.Vault is { } vault)
            vault.Changed -= OnVaultChanged;
        Explorer?.Dispose();
        Overlay = Explorer = null;
    }

    // ---- Claude -----------------------------------------------------------------------------------------------

    public override IEnumerable<IClaudeTool> ClaudeTools() => PostmanClaudeTools.Create(this);

    /// <summary>newman (Postman's runner) run from the shell: Karvon's run instead, whose results show in Karvon.</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
        [ClaudeShellRule.Command("newman", @"(npx\s+(--yes\s+|-y\s+)?)?newman\s+run\b", "Karvon runs Postman collections itself, with their scripts and tests and the environment chosen: use mcp__karvon__postman_run (postman_collections lists them), not newman.") with { InWorkspace = true }];

    /// <summary>What Claude did shows in Karvon: the request it sent with its response, the run it made.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        switch (output.Detail)
        {
            case PostmanSent sent:
                Open(sent.Item)?.Show(sent.Result);
                break;
            case PostmanRan ran:
                ShowRun(ran.Collection, ran.Folder).Show(ran.Results);
                break;
            case PostmanItem item when item.Collection.Of(item.Node) is { IsFolder: false } saved:
                Open(saved)?.Reload();
                break;
            default:
                if (tool is "postman_collections" && !output.IsError)
                    Workbench.ShowToolWindowBesideClaude(ToolWindowId);
                break;
        }
        return Task.CompletedTask;
    }

    // ---- Documents --------------------------------------------------------------------------------------------

    /// <summary>A request's document: the one open, else a new one.</summary>
    public PostmanRequestDocument? Open(PostmanItem item)
    {
        if (item.IsFolder)
            return null;
        var document = Workbench.Documents.OfType<PostmanRequestDocument>().FirstOrDefault(d => d.Is(item)) ?? new PostmanRequestDocument(this, item);
        Workbench.ShowDocument(document);
        return document;
    }

    public PostmanEnvironmentDocument Open(PostmanEnvironment environment)
    {
        var document = Workbench.Documents.OfType<PostmanEnvironmentDocument>().FirstOrDefault(d => d.File == environment.File) ?? new PostmanEnvironmentDocument(this, environment);
        Workbench.ShowDocument(document);
        return document;
    }

    /// <summary>The run of a collection, or of one of its folders.</summary>
    public PostmanRunDocument ShowRun(PostmanCollection collection, PostmanItem? folder)
    {
        var document = Workbench.Documents.OfType<PostmanRunDocument>().FirstOrDefault(d => d.File == collection.File && d.Folder == folder?.Path) ?? new PostmanRunDocument(this, collection, folder);
        Workbench.ShowDocument(document);
        return document;
    }

    public void ShowCookies()
    {
        var document = Workbench.Documents.OfType<PostmanCookiesDocument>().FirstOrDefault() ?? new PostmanCookiesDocument(this);
        Workbench.ShowDocument(document);
    }

    // ---- Sent by the user -------------------------------------------------------------------------------------

    /// <summary>The sender of the user's own requests: with the environment chosen, their cookies and tokens, asked before their values go to a host that is not the environment's.</summary>
    public PostmanSender Sender() => new(Library.Active, Library.Globals)
    {
        Folder = Workbench.Workspace, Cookies = Cookies, OAuth = OAuth, AskHost = AskHostAsync,
    };

    Task<bool> AskHostAsync(Uri uri, IReadOnlyCollection<string> names) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var environment = Library.Active ?? Library.Globals;
        var shown = string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "");
        if (await Workbench.ConfirmAsync("Send to another host", $"{uri.Authority} is not one of {environment?.Name ?? "the environment"}'s hosts. The request takes these of its values there:\n\n{shown}\n\nSend it, and add the host to the environment's?") != true)
            return false;
        environment?.AddHost(uri.Authority);
        Library.RaiseChanged();
        return true;
    });

    // ---- IPostmanHost -----------------------------------------------------------------------------------------

    string? IPostmanHost.Workspace => Workbench.Workspace;

    IEnumerable<DocumentTab> IPostmanHost.Documents => Workbench.Documents;

    void IPostmanHost.CloseDocument(DocumentTab document)
    {
        Workbench.RemoveDocument(document);
        document.Dispose();
    }

    Task<bool?> IPostmanHost.ConfirmAsync(string title, string message) => Workbench.ConfirmAsync(title, message);

    Task<IReadOnlyList<string>> IPostmanHost.PickFilesAsync(string title) => Workbench.PickFilesAsync(title);

    void IPostmanHost.OpenFile(string path) => Workbench.OpenFile(path);

    void IPostmanHost.Status(string text) => Workbench.Status(text);

    Task<bool> IPostmanHost.OpenSealedAsync() => Workbench.OpenSealedAsync();
}
