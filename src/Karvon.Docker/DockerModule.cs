using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Docker.ViewModels;
using Karvon.Docker.Views;
using Karvon.Modules;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Docker;

/// <summary>
/// Docker Explorer: the engine's containers, images, volumes and networks (see <see cref="DockerExplorerViewModel"/>), its
/// explorer made (and connected to the engine) the first time the tool window shows; what it opens are documents. It
/// knows the open solution's compose file and stack, writes compose and pull output to Output > Docker, opens database
/// containers in the Database Explorer, and gives Claude Code the container tools.
/// </summary>
public sealed partial class DockerModule : KarvonModule, IDockerHost, IDockerToolHost
{
    public const string ToolWindowId = "DockerExplorer";

    IClaudeService? _claude;
    DockerEngine? _engine;

    public override string Id => "docker";

    public override string Name => "Docker Explorer";


    public override string Icon => "docker";

    public override string Description =>
        "The Docker engine's containers (the solution's compose stack among them), images, volumes and networks: logs, a shell, "
        + "stats; database containers in the Database Explorer; Claude Code's container tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Docker Explorer", "Doc_ker Explorer", "Docker", DockSide.Right, () => new DockerExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<DockerDocument>(_ => new DockerDocumentView())];

    public override IDataTemplate OverlayTemplate => Template<DockerExplorerViewModel>(_ => new DockerOverlay());

    /// <summary>The explorer, once it has been needed (no docker command runs before).</summary>
    [ObservableProperty]
    public partial DockerExplorerViewModel? Explorer { get; private set; }

    /// <summary>Output > Docker: compose and pull.</summary>
    public OutputChannel Output { get; private set; } = null!;

    /// <summary>The explorer, made and connected to the engine the first time it is needed.</summary>
    public DockerExplorerViewModel EnsureExplorer()
    {
        if (Explorer is { } explorer)
            return explorer;
        Overlay = Explorer = explorer = new DockerExplorerViewModel(DockerEngine.Find(), this);
        _ = explorer.InitializeAsync();
        return explorer;
    }

    protected override void Started()
    {
        Output = Workbench.Output("Docker");
        _claude = Workbench.Service<IClaudeService>();
        // Another solution: its compose file and stack are the ones the explorer points out.
        Workbench.WorkspaceChanged += () =>
        {
            if (Explorer is { IsEngineRunning: true } explorer)
                _ = explorer.RefreshContainersAsync();
        };
    }

    /// <summary>Karvon closes: the explorer's engine streams stop.</summary>
    public override void Dispose()
    {
        Explorer?.Dispose();
        Overlay = Explorer = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => DockerClaudeTools.Create(this);

    /// <summary>What Docker's CLI does that the Docker tools do (not a build, nor an exec: those the shell does).</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("docker",
            @"(docker\s+(ps|logs|inspect|restart|rm|stats|container\s+(ls|list|logs|inspect|restart|rm))|docker(\s+|-)compose\s+(up|ps|logs))\b",
            "Karvon works with the user's Docker engine (Docker Explorer shows it): use mcp__karvon__container_list, container_logs, "
            + "container_inspect, container_restart, compose_up and container_remove, not the docker CLI."),
    ];

    public override async Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        switch (tool)
        {
            case "container_list" or "compose_up" or "container_remove":
                EnsureExplorer().OpenSection(DockerSection.Containers);
                break;
            case "container_logs" or "container_inspect" or "container_restart":
                await OpenContainerAsync(Str(input, "name"), tool == "container_inspect" ? "inspect" : null);
                break;
        }
    }

    /// <summary>A container's page (on its Inspect tab for an inspect), as the explorer opens it.</summary>
    async Task OpenContainerAsync(string? name, string? tab)
    {
        if (name is null)
            return;
        var docker = EnsureExplorer();
        if (!docker.Containers.AllRows.Any())
            await docker.RefreshContainersAsync();
        if (docker.Containers.AllRows.FirstOrDefault(r => r.Container.Name == name || r.Container.Id.StartsWith(name, StringComparison.Ordinal)) is { } row)
            docker.OpenContainer(row, tab);
    }

    // ---- IDockerToolHost ----------------------------------------------------------------------------------------

    string? IDockerToolHost.SolutionDirectory => Workbench.Workspace;

    DockerEngine IDockerToolHost.Docker => Explorer?.Engine ?? (_engine ??= DockerEngine.Find());

    bool IDockerToolHost.AllContainers => Workbench.Settings.ClaudeAllContainers;

    // ---- IDockerHost --------------------------------------------------------------------------------------------

    string? IDockerHost.SolutionDirectory => Workbench.Workspace;

    IEnumerable<DockerDocument> IDockerHost.Documents => Workbench.Documents.OfType<DockerDocument>();

    void IDockerHost.ShowDocument(DockerDocument document) => Workbench.ShowDocument(document);

    void IDockerHost.CloseDocument(DockerDocument document) => Workbench.CloseDocument(document);

    void IDockerHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IDockerHost.WriteOutput(string line) => Output.Append(line);

    void IDockerHost.ShowOutput() => Workbench.ShowOutput(Output);

    void IDockerHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    /// <summary>
    /// A database container as a data source of the Database Explorer: 127.0.0.1 and the port it publishes, with the
    /// user, password and database the image's variables set (POSTGRES_USER, MYSQL_ROOT_PASSWORD...). One already
    /// there for that port is reused.
    /// </summary>
    string? IDockerHost.OpenInDatabaseExplorer(DockerContainer container, JsonElement inspect)
    {
        if (Workbench.Service<IDatabaseService>() is not { } databases)
            return "The Database Explorer is off in Tools > Get Tools and Features.";
        if (DockerDatabases.ProviderOf(container.Image) is not { } name || !Enum.TryParse<DatabaseProvider>(name, out var provider))
            return $"{container.Image} is not a database the Database Explorer knows.";
        var env = new Dictionary<string, string>();
        if (inspect.TryGetProperty("Config", out var config) && config.TryGetProperty("Env", out var variables) && variables.ValueKind == JsonValueKind.Array)
            foreach (var item in variables.EnumerateArray().Select(v => v.GetString() ?? ""))
                if (item.IndexOf('=') is > 0 and var eq)
                    env[item[..eq]] = item[(eq + 1)..];
        var port = DockerDatabases.PortOf(name);
        int? hostPort = null;
        if (inspect.TryGetProperty("NetworkSettings", out var network) && network.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Object
            && ports.TryGetProperty($"{port}/tcp", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
            hostPort = bindings.EnumerateArray().Select(b => int.TryParse(DockerFormat.Str(b, "HostPort"), out var p) ? p : (int?)null).FirstOrDefault(p => p is not null);
        if (hostPort is null)
            return $"{container.Name} does not publish port {port}, so the Database Explorer cannot reach it. Run it with -p {port}:{port}, then try again.";
        string? Env(params string[] names) => names.Select(n => env.GetValueOrDefault(n)).FirstOrDefault(v => v is { Length: > 0 });
        var (user, password, database) = provider switch
        {
            DatabaseProvider.PostgreSql => (Env("POSTGRES_USER") ?? "postgres", Env("POSTGRES_PASSWORD"), Env("POSTGRES_DB") ?? Env("POSTGRES_USER") ?? "postgres"),
            DatabaseProvider.MySql => Env("MYSQL_USER", "MARIADB_USER") is { } u
                ? (u, Env("MYSQL_PASSWORD", "MARIADB_PASSWORD"), Env("MYSQL_DATABASE", "MARIADB_DATABASE"))
                : ("root", Env("MYSQL_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD"), Env("MYSQL_DATABASE", "MARIADB_DATABASE")),
            DatabaseProvider.SqlServer => ("sa", Env("MSSQL_SA_PASSWORD", "SA_PASSWORD"), "master"),
            _ => (Env("MONGO_INITDB_ROOT_USERNAME"), Env("MONGO_INITDB_ROOT_PASSWORD"), Env("MONGO_INITDB_DATABASE")),
        };
        databases.AddDataSource(new ServerProfile
        {
            Name = container.Name,
            Group = "Docker",
            Provider = provider,
            Host = "127.0.0.1",
            Port = hostPort,
            MaintenanceDatabase = database,
            Username = user,
            Password = password,
            SavePassword = password is not null,
            Comment = $"The Docker container {container.Name} ({container.Image})",
        }, s => s.Provider == provider && s.Port == hostPort && s.Host is "127.0.0.1" or "localhost");
        return null;
    }
}
