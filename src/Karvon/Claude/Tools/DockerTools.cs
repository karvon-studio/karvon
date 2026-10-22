using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Docker;
using Karvon.Docker.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

/// <summary>
/// The solution's containers for Claude, through the docker (or podman) command line: those compose started in the
/// solution's folder (all of them with no solution open, or when the user chose so), by container or compose service
/// name. Secrets in environments, commands and logs are hidden.
/// </summary>
abstract class DockerTool(IDockerToolHost host) : IClaudeTool
{
    protected IDockerToolHost Host { get; } = host;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(input, cancellationToken);
        }
        catch (Exception ex) when (ex is DockerException or InvalidOperationException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>The containers Claude may see: the solution's compose project, or every one.</summary>
    protected async Task<IReadOnlyList<DockerContainer>> ScopeAsync(CancellationToken cancellationToken)
    {
        var all = await Host.Docker.ContainersAsync(cancellationToken);
        return Host.SolutionDirectory is { } solution && !Host.AllContainers
            ? all.Where(c => Compose.IsInFolder(c.WorkingDir, solution)).ToList()
            : all;
    }

    protected string ScopeText => Host.SolutionDirectory is not null && !Host.AllContainers ? "the solution's compose project" : "the engine";

    /// <summary>What the solution's compose file gets wrong (a service that reaches another through localhost...).</summary>
    protected IReadOnlyList<ComposeProblem> ComposeProblems =>
        Host.SolutionDirectory is { } dir && Compose.FileIn(dir) is { } file ? Compose.Inspect(file) : [];

    /// <summary>The container the call names: by its name, then by compose service; or an error that lists them.</summary>
    protected async Task<DockerContainer> ContainerAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var name = Str(input, "name") ?? "";
        var scope = await ScopeAsync(cancellationToken);
        return scope.FirstOrDefault(c => c.Name == name) ?? scope.FirstOrDefault(c => c.Service == name)
            ?? throw new InvalidOperationException($"No container or compose service named '{name}' in {ScopeText}."
                + (scope.Count > 0 ? $" Containers: {string.Join(", ", scope.Select(c => c.Service is null ? c.Name : $"{c.Name} ({c.Service})"))}." : ""));
    }

    protected static JsonObject NameSchema(string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = description } },
        ["required"] = new JsonArray("name"),
    };
}

/// <summary><c>container_list</c>: the containers, running or stopped, with how they ended.</summary>
sealed class ContainerListTool(IDockerToolHost host) : DockerTool(host)
{
    public override string Name => "container_list";

    public override string Description =>
        "Lists the containers of the solution's compose project (every container when no solution is open), running or stopped: "
        + "name, compose service, image, state and status (exit codes included), and published ports; and the problems of the "
        + "solution's compose file (such as a service that reaches another through localhost).";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Containers", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope.Count == 0)
            return new ClaudeToolOutput($"No containers in {ScopeText}."
                + (Host.SolutionDirectory is { } dir && Compose.FileIn(dir) is { } file ? $" The compose file is {Path.GetFileName(file)}; compose_up starts it." : ""));
        var text = string.Join("\n", scope.Select(c => $"{c.Name}{(c.Service is null ? "" : $" (service {c.Service})")}: {c.State}, {c.Status}; image {c.Image}"
            + (c.Ports.Count > 0 ? $"; ports {string.Join(", ", c.Ports.Select(p => p.Text))}" : "")));
        if (ComposeProblems is { Count: > 0 } problems)
            text += "\n\nProblems of the compose file:\n" + string.Join("\n", problems.Select(p => "- " + p.Text));
        return new ClaudeToolOutput(text);
    }
}

/// <summary><c>container_logs</c>: the last lines of a container's output, also after it exited.</summary>
sealed class ContainerLogsTool(IDockerToolHost host) : DockerTool(host)
{
    public override string Name => "container_logs";

    public override string Description =>
        "Returns the last lines of a container's logs (standard output and error), also for a container that has exited. Accepts a "
        + "container name or a compose service name. Secrets in the output are hidden.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Container or compose service name."},
          "tail":{"type":"integer","description":"Number of lines from the end (default 200)."}},
         "required":["name"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Logs", Str(input, "name"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(input, cancellationToken);
        var tail = Int(input, "tail", 200, 1, 10_000);
        // Standard output and error as they came, interleaved.
        var lines = new List<string>();
        var result = await Host.Docker.RunAsync(["logs", "--tail", tail.ToString(System.Globalization.CultureInfo.InvariantCulture), container.Id],
            onLine: line =>
            {
                lock (lines)
                    lines.Add(line);
            }, cancellationToken: cancellationToken);
        if (!result.Succeeded)
            return ClaudeToolOutput.Error(Redaction.Text(result.Message));
        var logs = Redaction.Text(string.Join("\n", lines));
        return new ClaudeToolOutput(logs.Length == 0 ? $"{container.Name} has no log output." : $"Logs of {container.Name} ({container.Status}):\n{Tail(logs, 40_000)}");
    }
}

/// <summary><c>container_inspect</c>: what matters of a container's inspect, secrets hidden.</summary>
sealed class ContainerInspectTool(IDockerToolHost host) : DockerTool(host)
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override string Name => "container_inspect";

    public override string Description =>
        "Returns a container's state (status, exit code, error, restarts, health), command, environment (secrets hidden), ports, "
        + "mounts, networks, compose labels, and the compose file's problems for its service.";

    public override JsonObject InputSchema => NameSchema("Container or compose service name.");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Inspect", Str(input, "name"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(input, cancellationToken);
        var inspect = await Host.Docker.InspectAsync("container", container.Id, cancellationToken);
        var summary = Summary(inspect);
        summary["compose"]!["problems"] = new JsonArray([.. ComposeProblems.Where(p => p.Service == container.Service).Select(p => (JsonNode?)JsonValue.Create(p.Text))]);
        return new ClaudeToolOutput(summary.ToJsonString(Indented));
    }

    static JsonObject Summary(JsonElement c)
    {
        var state = c.TryGetProperty("State", out var s) ? s : default;
        var config = c.TryGetProperty("Config", out var cfg) ? cfg : default;
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var labels = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : [];
        var environment = new JsonArray();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
            foreach (var item in env.EnumerateArray().Select(e => e.GetString() ?? ""))
                environment.Add(item.IndexOf('=') is > 0 and var eq ? $"{item[..eq]}={Redaction.Variable(item[..eq], item[(eq + 1)..])}" : item);
        var ports = new List<string>();
        if (c.TryGetProperty("NetworkSettings", out var network) && network.TryGetProperty("Ports", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var port in map.EnumerateObject())
                if (port.Value.ValueKind == JsonValueKind.Array)
                    ports.AddRange(port.Value.EnumerateArray().Select(b => $"{Str(b, "HostIp")}:{Str(b, "HostPort")} -> {port.Name}"));
                else
                    ports.Add($"{port.Name} (not published)");
        JsonArray Strings(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? new JsonArray([.. v.EnumerateArray().Select(x => (JsonNode?)JsonValue.Create(Redaction.Text(x.GetString() ?? "")))])
                : [];
        var mounts = new JsonArray();
        if (c.TryGetProperty("Mounts", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var mount in m.EnumerateArray())
                mounts.Add($"{Str(mount, "Type")} {(Str(mount, "Type") == "volume" ? Str(mount, "Name") : Str(mount, "Source"))} -> {Str(mount, "Destination")}");
        return new JsonObject
        {
            ["name"] = (Str(c, "Name") ?? "").TrimStart('/'),
            ["service"] = labels.GetValueOrDefault("com.docker.compose.service"),
            ["image"] = Str(config, "Image"),
            ["state"] = new JsonObject
            {
                ["status"] = Str(state, "Status"),
                ["exitCode"] = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("ExitCode", out var code) ? code.GetInt32() : null,
                ["error"] = Str(state, "Error") is { Length: > 0 } error ? error : null,
                ["oomKilled"] = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("OOMKilled", out var oom) && oom.ValueKind == JsonValueKind.True,
                ["restartCount"] = c.TryGetProperty("RestartCount", out var restarts) ? restarts.GetInt32() : 0,
                ["startedAt"] = Str(state, "StartedAt"),
                ["finishedAt"] = Str(state, "FinishedAt"),
                ["health"] = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Health", out var health) && health.ValueKind == JsonValueKind.Object
                    ? Str(health, "Status") : null,
            },
            ["entrypoint"] = Strings(config, "Entrypoint"),
            ["command"] = Strings(config, "Cmd"),
            ["workingDir"] = Str(config, "WorkingDir"),
            ["environment"] = environment,
            ["ports"] = new JsonArray([.. ports.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["mounts"] = mounts,
            ["networks"] = network.ValueKind == JsonValueKind.Object && network.TryGetProperty("Networks", out var networks) && networks.ValueKind == JsonValueKind.Object
                ? new JsonArray([.. networks.EnumerateObject().Select(n => (JsonNode?)JsonValue.Create(n.Name))])
                : [],
            ["compose"] = new JsonObject
            {
                ["project"] = labels.GetValueOrDefault("com.docker.compose.project"),
                ["configFiles"] = labels.GetValueOrDefault("com.docker.compose.project.config_files"),
            },
        };
    }
}

/// <summary><c>container_restart</c> (asks first): restarts a container, or starts one that stopped.</summary>
sealed class ContainerRestartTool(IDockerToolHost host) : DockerTool(host)
{
    public override string Name => "container_restart";

    public override string Description =>
        "Restarts a container, or starts it when it stopped. The user approves it first. It runs with its current configuration: "
        + "after changing the compose file, use compose_up.";

    public override JsonObject InputSchema => NameSchema("Container or compose service name.");

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Restart", Str(input, "name"), $"Allow Claude to restart the container {Str(input, "name")}?")
        {
            Warning = "It starts again with its current configuration.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(input, cancellationToken);
        var result = await Host.Docker.RunAsync([container.IsUp ? "restart" : "start", container.Id], cancellationToken: cancellationToken);
        if (!result.Succeeded)
            return ClaudeToolOutput.Error(Redaction.Text(result.Message));
        var after = (await Host.Docker.ContainersAsync(cancellationToken)).FirstOrDefault(c => c.Id == container.Id);
        return new ClaudeToolOutput($"{container.Name}: {after?.Status ?? "restarted"}");
    }
}

/// <summary><c>compose_up</c> (asks first): compose up -d in the solution's folder, for every service or one.</summary>
sealed class ComposeUpTool(IDockerToolHost host) : DockerTool(host)
{
    public override string Name => "compose_up";

    public override string Description =>
        "Runs compose up -d for the solution's compose file, or for one service: services whose configuration changed are "
        + "recreated. The user approves it first. Returns the output and the containers' states.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"service":{"type":"string","description":"One service to start or recreate; leave out for every service."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Compose Up", Str(input, "service") ?? "every service",
            Str(input, "service") is { } service ? $"Allow Claude to run compose up -d {service}?" : "Allow Claude to run compose up -d for the solution?")
        {
            Warning = "Services whose configuration changed are recreated.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Host.SolutionDirectory is not { } folder || Compose.FileIn(folder) is null)
            return ClaudeToolOutput.Error("The open solution has no compose file (compose.yaml, docker-compose.yml) in its folder.");
        List<string> args = ["compose", "up", "-d"];
        if (Str(input, "service") is { } service)
            args.Add(service);
        var result = await Host.Docker.RunAsync(args, folder, TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
        var text = new StringBuilder(Redaction.Text(Tail((result.Output + "\n" + result.Error).Trim(), 20_000))).AppendLine().AppendLine();
        foreach (var c in await ScopeAsync(cancellationToken))
            text.AppendLine($"{c.Name}: {c.Status}");
        return new ClaudeToolOutput(text.ToString().TrimEnd(), !result.Succeeded);
    }
}

/// <summary><c>container_remove</c> (asks first, every time): stops and deletes a container.</summary>
sealed class ContainerRemoveTool(IDockerToolHost host) : DockerTool(host)
{
    public override string Name => "container_remove";

    public override string Description => "Removes a container (stopping it first). Its writable layer is lost; volumes stay. The user approves it first.";

    public override JsonObject InputSchema => NameSchema("Container or compose service name.");

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Destructive, "Remove Container", Str(input, "name"), $"Allow Claude to remove the container {Str(input, "name")}?")
        {
            Warning = "It is stopped and deleted: its writable layer is lost; volumes stay.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(input, cancellationToken);
        var result = await Host.Docker.RunAsync(["rm", "-f", container.Id], cancellationToken: cancellationToken);
        return result.Succeeded ? new ClaudeToolOutput($"Removed {container.Name}.") : ClaudeToolOutput.Error(Redaction.Text(result.Message));
    }
}
