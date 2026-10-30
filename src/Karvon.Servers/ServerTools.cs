using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Servers.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Servers;

/// <summary>Claude Code's server tools.</summary>
public static class ServerClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<ServersExplorerViewModel> servers) =>
        [new ServerListTool(servers), new ServerLogsTool(servers), new ServerExecTool(servers)];
}

/// <summary>
/// The Servers explorer's machines for Claude, through Karvon's own SSH connections (the user's keys, and passwords asked in
/// Karvon, never shown to Claude). A production machine's commands are destructive: each asks, and is never remembered.
/// </summary>
abstract class ServerTool(Func<ServersExplorerViewModel> servers) : IClaudeTool
{
    protected ServersExplorerViewModel Servers => servers();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    /// <summary>The server a call names.</summary>
    public string? Target(JsonElement input) => Str(input, "server");

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is ServerException or InvalidOperationException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>The machine the call names, of those Claude may reach in this folder, or an error that lists them.</summary>
    protected ServerHost Server(JsonElement input)
    {
        var name = Str(input, "server") ?? "";
        var reachable = Reachable();
        if (reachable.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } server)
            return server.Host;
        throw new InvalidOperationException(Servers.Find(name) is not null
            ? $"{name} is not one of the servers Claude may reach in this folder. {LetIn}"
            : reachable.Count > 0
                ? $"There is no server named '{name}' for this folder. Servers: {string.Join(", ", reachable.Select(s => s.Name))}."
                : NoServers());
    }

    /// <summary>The servers the user let Claude reach in the folder it works in.</summary>
    protected List<ServerNode> Reachable() => [.. Servers.Servers.Where(s => Servers.IsForClaude(s.Host))];

    protected const string LetIn = "The user lets Claude reach a server in the Servers window (right-click > Let Claude Reach It Here): ask them.";

    protected string NoServers() => Servers.Servers.Any()
        ? $"No server is Claude's to reach in this folder (the user has {Servers.Servers.Count()}). {LetIn}"
        : "There are no servers. The user adds them in ~/.ssh/config or Karvon's Servers window.";

    protected bool IsProduction(JsonElement input) => Str(input, "server") is { } name && Servers.Find(name)?.IsProduction == true;

    protected static JsonObject ServerProperty => new() { ["type"] = "string", ["description"] = "The server's name, from server_list." };
}

/// <summary><c>server_list</c>: the machines, where they are, which are production.</summary>
sealed class ServerListTool(Func<ServersExplorerViewModel> servers) : ServerTool(servers)
{
    public override string Name => "server_list";

    public override string Description =>
        "Lists the servers the user lets Claude reach in this folder (SSH machines from ~/.ssh/config and Karvon's Servers window): "
        + "name, user@host:port, and which are production, where every command asks the user.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Servers", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var all = Reachable().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0)
            return Task.FromResult(new ClaudeToolOutput(NoServers()));
        return Task.FromResult(new ClaudeToolOutput(string.Join("\n", all.Select(s => $"{s.Name}: {s.Target}"
            + (s.Host.Group is { } group ? $", group {group}" : "") + (s.IsProduction ? ", production (every command asks the user)" : "")))));
    }
}

/// <summary><c>server_logs</c>: a unit's journal, the whole journal, or the end of a log file.</summary>
sealed class ServerLogsTool(Func<ServersExplorerViewModel> servers) : ServerTool(servers)
{
    public override string Name => "server_logs";

    public override string Description => "Returns a server's logs: a systemd unit's journal, the whole journal, or the end of a log file. Secrets in them are hidden.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = ServerProperty,
            ["unit"] = new JsonObject { ["type"] = "string", ["description"] = "A systemd unit, such as nginx or my-api." },
            ["file"] = new JsonObject { ["type"] = "string", ["description"] = "A log file's path instead of the journal, such as /var/log/app.log." },
            ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "How many lines from the end (default 200)." },
        },
        ["required"] = new JsonArray("server"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Server Logs", $"{Str(input, "server")}: {Str(input, "file") ?? Str(input, "unit") ?? "the journal"}", "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var host = Server(input);
        var result = await Servers.Connections.RunAsync(host, RemoteCommands.Logs(Str(input, "unit"), Str(input, "file"), Int(input, "lines", 200, 1, 5000)),
            TimeSpan.FromSeconds(60), cancellationToken);
        var text = Redaction.Text(Tail(result.Succeeded ? result.Output : result.Combined, 40_000));
        return text.Length == 0 ? new ClaudeToolOutput("No log lines.", !result.Succeeded) : new ClaudeToolOutput(text, !result.Succeeded) { Outside = $"the logs of {host.Name}" };
    }
}

/// <summary><c>server_exec</c> (asks first; on production, destructive): a command through the machine's login shell.</summary>
sealed class ServerExecTool(Func<ServersExplorerViewModel> servers) : ServerTool(servers)
{
    public override string Name => "server_exec";

    public override string Description =>
        "Runs a shell command on a server and returns its exit code and output. The user approves each command; on a production "
        + "server it is destructive and always asks.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = ServerProperty,
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The command, run by the server's login shell." },
        },
        ["required"] = new JsonArray("server", "command"),
    };

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var production = IsProduction(input);
        return new(production ? ToolAccess.Destructive : ToolAccess.Write, "Run on Server", $"{Str(input, "server")}: {OneLine(Str(input, "command") ?? "")}",
            $"Allow Claude to run this on {Str(input, "server")}{(production ? " (production)" : "")}?")
        {
            Code = Str(input, "command"),
            Warning = production ? $"{Str(input, "server")} is production." : null,
        };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var host = Server(input);
        var command = Str(input, "command") ?? throw new InvalidOperationException("Give the command to run.");
        var result = await Servers.Connections.RunAsync(host, command, TimeSpan.FromMinutes(2), cancellationToken);
        return new ClaudeToolOutput(Redaction.Text(Tail($"Exit code {result.ExitCode}.\n{result.Combined}".TrimEnd(), 40_000)), !result.Succeeded)
        {
            Outside = $"the command's output on {host.Name}",
        };
    }
}
