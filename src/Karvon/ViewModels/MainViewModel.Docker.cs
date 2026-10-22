using System.Text.Json;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Docker;
using Karvon.Docker.ViewModels;

namespace Karvon.ViewModels;

/// <summary>
/// View > Docker Explorer: a tool window with the engine's containers, images, volumes and networks (see
/// <see cref="DockerExplorerViewModel"/>), made the first time it shows; what it opens are documents. It knows the
/// open solution's compose file and stack, writes compose and pull output to Output > Docker, opens database
/// containers in the solution's Database Explorer and asks Claude about a container.
/// </summary>
public partial class MainViewModel : IDockerHost
{
    public OutputChannel DockerOutput { get; } = new("Docker");

    /// <summary>The explorer, once the tool window has shown (null before: no docker command runs until then).</summary>
    [ObservableProperty]
    public partial DockerExplorerViewModel? Docker { get; private set; }

    /// <summary>The explorer, made and connected to the engine the first time it is needed.</summary>
    public DockerExplorerViewModel EnsureDocker()
    {
        if (Docker is { } docker)
            return docker;
        docker = Docker = new DockerExplorerViewModel(DockerEngine.Find(), this);
        _ = docker.InitializeAsync();
        return docker;
    }

    [RelayCommand]
    void ShowDockerExplorer()
    {
        EnsureDocker();
        ShowToolWindow(ToolWindows.DockerExplorer);
    }

    /// <summary>Another solution: its compose file and stack are the ones the explorer points out.</summary>
    Task OnDockerSolutionChangedAsync() => Docker is { IsEngineRunning: true } explorer ? explorer.RefreshContainersAsync() : Task.CompletedTask;

    /// <summary>Karvon closes: the explorer's engine streams stop.</summary>
    public void ShutdownDocker()
    {
        Docker?.Dispose();
        Docker = null;
    }

    IEnumerable<DockerDocument> IDockerHost.Documents => Documents.OfType<DockerDocument>();

    void IDockerHost.ShowDocument(DockerDocument document)
    {
        if (!Documents.Contains(document))
            Documents.Add(document);
        ActiveDocument = document;
    }

    void IDockerHost.CloseDocument(DockerDocument document)
    {
        if (Documents.Contains(document))
            _ = CloseDocument(document);
    }

    string? IDockerHost.SolutionDirectory => Solution?.Directory;

    void IDockerHost.OpenUrl(string url) => OpenBrowser(url);

    void IDockerHost.WriteOutput(string line) => DockerOutput.Append(line);

    void IDockerHost.ShowOutput()
    {
        SelectedOutput = DockerOutput;
        ShowToolWindow(ToolWindows.Output);
    }

    void IDockerHost.AskClaude(string prompt)
    {
        ShowToolWindow(ToolWindows.Claude);
        Claude.Prompt = prompt;
        Claude.RequestFocus();
    }

    /// <summary>
    /// A database container as a data source of the Database Explorer: 127.0.0.1 and the port it publishes, with the
    /// user, password and database the image's variables set (POSTGRES_USER, MYSQL_ROOT_PASSWORD...). One already
    /// there for that port is reused.
    /// </summary>
    string? IDockerHost.OpenInDatabaseExplorer(DockerContainer container, JsonElement inspect)
    {
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
        var store = DatabaseServers;
        if (!store.List().Any(s => s.Provider == provider && s.Port == hostPort && s.Host is "127.0.0.1" or "localhost"))
            store.Upsert(new ServerProfile
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
            });
        if (_database is { } explorer)
            explorer.ReloadServers();
        ShowToolWindow(ToolWindows.DatabaseExplorer);
        return null;
    }
}
