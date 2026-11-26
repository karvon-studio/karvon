using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude;
using Karvon.Claude.Tools;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Machine;

/// <summary>
/// <c>machine_ports</c>: the ports this computer listens on, each with the process that holds it, or whether one port is
/// taken (and by what). Command lines have their secrets hidden.
/// </summary>
sealed class MachinePortsTool(Func<IReadOnlyList<ListeningPort>> list) : IClaudeTool
{
    public string Name => "machine_ports";

    public string Description =>
        "Lists the ports this computer listens on (TCP listening, UDP bound): each with its address (all addresses, or this "
        + "computer only) and the process that holds it (its name, pid, user and command line). Give a port to see whether it "
        + "is taken, and by what (an app that will not start because its port is in use). Use it instead of ss, netstat or lsof.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["port"] = new JsonObject { ["type"] = "integer", ["description"] = "Only this port (1 to 65535)." },
        },
    };

    public ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Ports", Int(input, "port", 0, 0, 65535) is > 0 and var port ? $"port {port}" : "this computer", "");

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var port = Int(input, "port", 0, 0, 65535);
        var ports = (await Task.Run(list, cancellationToken)).Where(p => port == 0 || p.Port == port).ToList();
        if (ports.Count == 0)
            return new ClaudeToolOutput(port > 0 ? $"Nothing listens on port {port}: it is free." : "This computer listens on no port.");
        var text = new StringBuilder(port > 0 ? $"Port {port} is taken:" : $"This computer listens on {ports.Count} port(s):");
        foreach (var p in ports)
            text.Append('\n').Append(Row(p));
        return new ClaudeToolOutput(text.ToString());
    }

    /// <summary>"TCP 0.0.0.0:5000 (all addresses): dotnet, pid 1234, user nbk: dotnet run".</summary>
    internal static string Row(ListeningPort p) =>
        $"{p.Protocol} {(p.Address.Contains(':') ? $"[{p.Address}]" : p.Address)}:{p.Port} ({(p.IsEverywhere ? "all addresses" : p.IsLocalOnly ? "this computer only" : "this address")}): "
        + (p.Pid is { } pid ? $"{p.Process ?? "?"}, pid {pid}" : "a process of another user's or the system's")
        + (p.User is { Length: > 0 } user ? $", user {user}" : "")
        + (p.CommandLine is { Length: > 0 } command ? ": " + Redaction.Text(command.Length > 300 ? command[..300] + "…" : command) : "");
}

/// <summary><c>machine_processes</c>: the processes, by the CPU or memory they use (the CPU over a second), with their ports.</summary>
sealed class MachineProcessesTool : IClaudeTool
{
    public string Name => "machine_processes";

    public string Description =>
        "Lists this computer's processes, those using the most CPU (over a second) or memory first: each with its pid, user, CPU "
        + "share, memory, the ports it listens on and its command line; and the whole machine's CPU and memory. Give name to see "
        + "only the processes whose name or command line has it. Use it instead of ps or top.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Only processes whose name or command line has this." },
            ["sort"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("cpu", "memory"), ["description"] = "cpu (default) or memory." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "How many at most (default 25, up to 200)." },
        },
    };

    public ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Processes", Str(input, "name"), "");

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var sampler = new ProcessSampler();
        var resources = new ResourceSampler();
        await Task.Run(() => resources.Sample(sampler.Sample()), cancellationToken);
        await Task.Delay(1000, cancellationToken);
        var (processes, usage, ports) = await Task.Run(() =>
        {
            var processes = sampler.Sample();
            return (processes, resources.Sample(processes), Listening.All());
        }, cancellationToken);
        var name = Str(input, "name");
        var byPid = ports.Where(p => p.Pid is not null).ToLookup(p => p.Pid);
        var found = processes.Where(p => name is null || p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
            || (p.CommandLine ?? "").Contains(name, StringComparison.OrdinalIgnoreCase));
        var sorted = (Str(input, "sort") == "memory" ? found.OrderByDescending(p => p.Memory) : found.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory)).ToList();
        var limit = Int(input, "limit", 25, 1, 200);
        var text = new StringBuilder($"CPU {usage.Cpu.Total:0.0}% of {Environment.ProcessorCount} CPUs; memory {ResourceSampler.Bytes(usage.Memory.Used)} of "
            + $"{ResourceSampler.Bytes(usage.Memory.Total)}; {processes.Count} processes.");
        text.Append(name is null ? "" : $" {sorted.Count} match \"{name}\".");
        foreach (var p in sorted.Take(limit))
        {
            var listens = string.Join(", ", byPid[p.Pid].Select(l => l.Port).Distinct().Order());
            text.Append($"\npid {p.Pid} {p.Name}").Append(p.User is { } user ? $" (user {user})" : "")
                .Append($": CPU {p.Cpu:0.0}%, {ResourceSampler.Bytes(p.Memory)}")
                .Append(listens.Length > 0 ? $", listens on {listens}" : "")
                .Append(p.CommandLine is { Length: > 0 } command ? ": " + Redaction.Text(command.Length > 200 ? command[..200] + "…" : command) : "");
        }
        if (sorted.Count > limit)
            text.Append($"\n… and {sorted.Count - limit} more.");
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>machine_stop_process</c>: a process stopped, the user asked first (it is not undone).</summary>
sealed class MachineStopProcessTool : IClaudeTool
{
    public string Name => "machine_stop_process";

    public string Description =>
        "Stops a process of this computer by its pid (one that holds a port another program needs, one that hangs). The user is "
        + "asked each time; what it has not saved is lost. Find the pid with machine_processes or machine_ports.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["pid"] = new JsonObject { ["type"] = "integer", ["description"] = "The process's id." } },
        ["required"] = new JsonArray("pid"),
    };

    static (string Name, string? Command)? Find(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var command = OperatingSystem.IsLinux() ? Read($"/proc/{pid}/cmdline")?.Replace('\0', ' ').Trim() : null;
            return (process.ProcessName, command);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? Refusal(JsonElement input) => Int(input, "pid", 0, 0, int.MaxValue) switch
    {
        <= 1 => "Give the pid of the process to stop.",
        var pid when pid == Environment.ProcessId => "That is Karvon itself: it is not stopped from a tool.",
        var pid when Find(pid) is null => $"No process {pid} runs.",
        _ => null,
    };

    public ClaudeToolPlan Plan(JsonElement input)
    {
        var pid = Int(input, "pid", 0, 0, int.MaxValue);
        var found = Find(pid);
        return new(ToolAccess.Destructive, "Stop Process", $"{found?.Name ?? "?"} ({pid})", $"Allow Claude to stop {found?.Name ?? "a process"} (process {pid})?")
        {
            Code = found?.Command is { Length: > 0 } command ? Redaction.Text(command) : null,
            Warning = "It ends at once: what it has not saved is lost.",
        };
    }

    public Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pid = Int(input, "pid", 0, 0, int.MaxValue);
        if (Refusal(input) is { } refusal)
            return Task.FromResult(ClaudeToolOutput.Error(refusal));
        var name = Find(pid)?.Name ?? "?";
        return Task.FromResult(ViewModels.MachineExplorerViewModel.StopProcess(pid) is { } problem
            ? ClaudeToolOutput.Error(problem)
            : new ClaudeToolOutput($"Process {pid} ({name}) is stopped."));
    }
}

/// <summary><c>machine_resources</c>: the CPU (over a second), memory, swap, disks and network, and how long it has been up.</summary>
sealed class MachineResourcesTool : IClaudeTool
{
    public string Name => "machine_resources";

    public string Description =>
        "What this computer uses now: its CPU (in all and each core, over a second), memory and swap, each disk's space (used and "
        + "free) and each network interface's traffic, and how long it has been up. Use it to see whether a disk is full or memory short.";

    public JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Resources", "this computer", "");

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var sampler = new ProcessSampler();
        var resources = new ResourceSampler();
        await Task.Run(() => resources.Sample(sampler.Sample()), cancellationToken);
        await Task.Delay(1000, cancellationToken);
        var usage = await Task.Run(() => resources.Sample(sampler.Sample()), cancellationToken);
        var text = new StringBuilder($"CPU {usage.Cpu.Total:0.0}% of {Environment.ProcessorCount} CPUs");
        if (usage.Cpu.Cores.Count > 0)
            text.Append(" (each: ").Append(string.Join(", ", usage.Cpu.Cores.Select(c => $"{c:0}%"))).Append(')');
        var memory = usage.Memory;
        text.Append($".\nMemory: {ResourceSampler.Bytes(memory.Used)} of {ResourceSampler.Bytes(memory.Total)} used");
        if (memory.SwapTotal > 0)
            text.Append($"; swap {ResourceSampler.Bytes(memory.SwapUsed)} of {ResourceSampler.Bytes(memory.SwapTotal)}");
        text.Append(".\nDisks:");
        foreach (var disk in usage.Disks)
            text.Append($"\n  {disk.Mount} ({disk.Format}): {ResourceSampler.Bytes(disk.Used)} of {ResourceSampler.Bytes(disk.Total)} used ({disk.Share:0}%), {ResourceSampler.Bytes(disk.Free)} free");
        if (usage.Network.Count > 0)
        {
            text.Append("\nNetwork:");
            foreach (var nic in usage.Network)
                text.Append($"\n  {nic.Name}: {ResourceSampler.Bytes(nic.ReceivedPerSecond)}/s in, {ResourceSampler.Bytes(nic.SentPerSecond)}/s out");
        }
        text.Append($"\nUp for {ViewModels.ResourcesPage.Took(usage.Uptime)}.");
        return new ClaudeToolOutput(text.ToString());
    }
}
