using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Ports.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Ports;

/// <summary>
/// Claude Code's tools for the Ports window: what is forwarded, a port forwarded (asking the user each time: it makes the
/// port public), a forward stopped. Through Karvon's own tunnel client and the API key the keyring keeps, which Claude
/// never sees.
/// </summary>
public static class PortsClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<PortsViewModel> ports) => [new TunnelListTool(ports), new TunnelOpenTool(ports), new TunnelCloseTool(ports)];

    /// <summary>A forward as Claude reads it.</summary>
    internal static string Row(PortForward f) =>
        $"localhost:{f.Port} {f.ProtocolText}: {f.StateText}" + (f.Address is { } address && f.IsOnline ? $" at {address}, {f.Connections} connection(s)" : "")
        + (f.Error is { } error ? $" ({error})" : "");
}

abstract class PortsTool(Func<PortsViewModel> ports) : IClaudeTool
{
    protected PortsViewModel Ports => ports();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    protected static int Port(JsonElement input) =>
        Int(input, "port", 0, 0, 65535) is var port and > 0 ? port : throw new InvalidOperationException("'port' is required: the local port (1 to 65535).");

    protected static JsonObject PortProperty => new() { ["type"] = "integer", ["description"] = "The local port (the app listens on localhost:port)." };
}

/// <summary><c>tunnel_list</c>: the forwarded ports, and the running program's that are not.</summary>
sealed class TunnelListTool(Func<PortsViewModel> ports) : PortsTool(ports)
{
    public override string Name => "tunnel_list";

    public override string Description =>
        "Lists the ports forwarded in Karvon's Ports window (their public address, protocol, state and connections), the ports the "
        + "program Karvon runs listens on that are not forwarded, and the tunnel service.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Ports", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var text = new StringBuilder($"Tunnel service: {Ports.ServiceName}" + (Ports.HasKey ? "" : " (no API key yet: the user sets it in View > Ports)") + ".");
        text.Append(Ports.Forwards.Count == 0 ? "\nNo ports are forwarded." : "\n" + string.Join("\n", Ports.Forwards.Select(PortsClaudeTools.Row)));
        if (Ports.Detected.Count > 0)
            text.Append("\nThe running program listens on (not forwarded): ").Append(string.Join(", ", Ports.Detected.Select(d => d.Url))).Append('.');
        return Task.FromResult(new ClaudeToolOutput(text.ToString()));
    }
}

/// <summary><c>tunnel_open</c>: a local port at a public address (asks the user: it makes the port public).</summary>
sealed class TunnelOpenTool(Func<PortsViewModel> ports) : PortsTool(ports)
{
    public override string Name => "tunnel_open";

    public override string Description =>
        "Forwards a local port to a public address through Karvon's tunnel service (as Visual Studio Code's port forwarding): "
        + "HTTP gives https://<name>.<domain> (the service's TLS in front of the plain local port), TCP a host and port (SSH, a "
        + "database, an HTTPS port as it is). Anyone with the address reaches the port while it is forwarded; the user approves "
        + "each call. Returns the public address.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["port"] = PortProperty,
            ["protocol"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("http", "tcp"), ["description"] = "http (default) or tcp." },
            ["subdomain"] = new JsonObject { ["type"] = "string", ["description"] = "For http: the name in https://<name>.<domain> (default: the API key's own name)." },
            ["public_port"] = new JsonObject { ["type"] = "integer", ["description"] = "For tcp: a fixed public port, when the service allows it (default: any)." },
        },
        ["required"] = new JsonArray("port"),
    };

    static string Protocol(JsonElement input) => Str(input, "protocol")?.ToLowerInvariant() == "tcp" ? "tcp" : "http";

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var port = Int(input, "port", 0, 0, 65535);
        return new(ToolAccess.Write, "Forward Port", $"localhost:{port} ({Protocol(input).ToUpperInvariant()})", $"Allow Claude to make localhost:{port} public?")
        {
            Warning = $"Anyone with its address reaches localhost:{port} through {Ports.ServiceName} until it is stopped (in View > Ports).",
        };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var port = Port(input);
        var protocol = Protocol(input);
        var forward = await Ports.ForwardAsync(port, protocol, (Str(input, "subdomain") ?? "").Trim().ToLowerInvariant(),
            protocol == "tcp" ? (ushort)Int(input, "public_port", 0, 0, 65535) : (ushort)0, ask: false);
        return forward.IsOnline
            ? new ClaudeToolOutput($"localhost:{port} is forwarded ({forward.ProtocolText}) at {forward.Address}.")
            : ClaudeToolOutput.Error($"localhost:{port} is not forwarded: {forward.Error ?? forward.StateText}");
    }
}

/// <summary><c>tunnel_close</c>: a forward stopped (it stays in the list, to forward again).</summary>
sealed class TunnelCloseTool(Func<PortsViewModel> ports) : PortsTool(ports)
{
    public override string Name => "tunnel_close";

    public override string Description => "Stops forwarding a local port: its public address no longer reaches it.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["port"] = PortProperty },
        ["required"] = new JsonArray("port"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Stop Forwarding", $"localhost:{Int(input, "port", 0, 0, 65535)}", "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var port = Port(input);
        if (Ports.Find(port) is not { } forward)
            return ClaudeToolOutput.Error($"localhost:{port} is not forwarded.");
        await Ports.StopAsync(forward);
        return new ClaudeToolOutput($"localhost:{port} is no longer forwarded.");
    }
}
