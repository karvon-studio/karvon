using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Editor;
using Karvon.Http.ViewModels;
using Karvon.Http.Views;
using Karvon.Modules;

namespace Karvon.Http;

/// <summary>
/// The HTTP client: a .http file's requests sent from the editor (▶ beside each, or Send Request), their responses in a
/// pane beside it, http-client.env.json's environments; the Endpoints Explorer of the solution's ASP.NET Core endpoints
/// (each one a request of its project's .http file). Claude Code gets the HTTP tools.
/// </summary>
public sealed partial class HttpModule : KarvonModule, IHttpHost
{
    public const string ToolWindowId = "Endpoints";

    readonly Dictionary<string, HttpPane> _panes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _environments = new(StringComparer.Ordinal);

    public override string Id => "http";

    public override string Name => "HTTP Client";

    public override string Icon => "http";

    public override string Description =>
        "Visual Studio's HTTP editor: a .http file's requests sent from the editor, their responses beside it, http-client.env.json's "
        + "environments and variables; the Endpoints Explorer of the solution's ASP.NET Core endpoints; Claude Code's HTTP tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Endpoints Explorer", "_Endpoints Explorer", "ToolEndpoints", DockSide.Right, () => new EndpointsView { DataContext = EnsureEndpoints() })];

    [ObservableProperty]
    public partial EndpointsViewModel? Endpoints { get; private set; }

    internal new IWorkbench Workbench => base.Workbench;

    public EndpointsViewModel EnsureEndpoints()
    {
        if (Endpoints is null)
        {
            Endpoints = new EndpointsViewModel(this);
            _ = Endpoints.RefreshAsync();
        }
        return Endpoints;
    }

    protected override void Started()
    {
        Workbench.FileSaved += OnFileSaved;
        Workbench.WorkspaceChanged += OnWorkspaceChanged;
    }

    void OnFileSaved(string path) => Endpoints?.FileSaved(path);

    void OnWorkspaceChanged() => _ = Endpoints?.RefreshAsync();

    public override void Dispose()
    {
        Workbench.FileSaved -= OnFileSaved;
        Workbench.WorkspaceChanged -= OnWorkspaceChanged;
        Endpoints = null;
    }

    public static bool IsHttpFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".http" or ".rest";

    /// <summary>A .http file's editor gets the response pane (its environment as last chosen for the file).</summary>
    public override EditorPane? EditorPane(EditorHost host)
    {
        if (!IsHttpFile(host.FilePath))
            return null;
        var pane = new HttpPane(host, () => Workbench.Workspace, _environments.GetValueOrDefault(host.FilePath), () => _panes.Remove(host.FilePath));
        pane.Model.EnvironmentChosen += env => _environments[host.FilePath] = env;
        _panes[host.FilePath] = pane;
        return pane;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => HttpClaudeTools.Create(this);

    /// <summary>A .http file's request (by its name, or the line it is at; else the file's first) and the variables to send it with.</summary>
    internal (HttpRequestBlock Block, HttpVariables Variables) Request(string file, string? request, string? environment)
    {
        var path = Path.GetFullPath(file, Workbench.Workspace ?? Directory.GetCurrentDirectory());
        if (_panes.TryGetValue(path, out var pane))
        {
            if (environment is not null && pane.Model.Environments.FirstOrDefault(e => e.Name == environment) is { } chosen)
                pane.Model.Environment = chosen;
            return (Find(pane.Model.Requests, request, path), pane.Model.Variables());
        }
        if (!File.Exists(path))
            throw new InvalidOperationException($"There is no {file}.");
        var (requests, variables) = HttpFile.Parse(File.ReadAllText(path));
        var environments = HttpEnvironments.Load(path, Workbench.Workspace);
        var env = environment ?? _environments.GetValueOrDefault(path);
        return (Find(requests, request, path), new HttpVariables(variables,
            env is not null && environments.TryGetValue(env, out var values) ? values : new Dictionary<string, string>(),
            new Dictionary<string, HttpExchange>(), Path.GetDirectoryName(path)));
    }

    static HttpRequestBlock Find(IReadOnlyList<HttpRequestBlock> requests, string? request, string path) =>
        (request is null ? requests.FirstOrDefault()
            : int.TryParse(request, out var line) ? HttpFile.At(requests, line)
            : requests.FirstOrDefault(r => r.Name == request))
        ?? throw new InvalidOperationException(request is null ? $"{Path.GetFileName(path)} has no request." : $"{Path.GetFileName(path)} has no request {request}.");

    /// <summary>A .http file's request sent: through its pane when it is open (the response shows there), else as the file is on disk.</summary>
    internal async Task<HttpExchange> SendAsync(string file, string? request, string? environment, CancellationToken ct)
    {
        var (block, variables) = Request(file, request, environment);
        var path = Path.GetFullPath(file, Workbench.Workspace ?? Directory.GetCurrentDirectory());
        return _panes.TryGetValue(path, out var pane) ? await pane.Model.SendAsync(block, ct) : await HttpRunner.SendAsync(block, variables, ct);
    }

    // ---- IHttpHost -------------------------------------------------------------------------------------------

    string? IHttpHost.Workspace => Workbench.Workspace;

    void IHttpHost.OpenFile(string path, int line) => Workbench.OpenFile(path, line);

    void IHttpHost.Status(string text) => Workbench.Status(text);
}
