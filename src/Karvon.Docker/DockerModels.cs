using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Karvon.Docker;

/// <summary>A published (or only exposed) port of a container: <c>0.0.0.0:8080->80/tcp</c>.</summary>
public sealed record DockerPort(int? HostPort, int ContainerPort, string Protocol)
{
    /// <summary>"8080:80" as Docker Desktop shows it, or "80" when the port is not published.</summary>
    public string Text => HostPort is { } host ? $"{host}:{ContainerPort}" : ContainerPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>Where a browser reaches it, when it is published over TCP.</summary>
    public string? Url => HostPort is { } host && Protocol == "tcp" ? $"http://localhost:{host}" : null;
}

/// <summary>A container as <c>ps</c> lists it, its compose labels, and when it last started (from <c>inspect</c>).</summary>
public sealed partial record DockerContainer(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<DockerPort> Ports,
    string Command,
    DateTimeOffset? Created,
    IReadOnlyList<string> Mounts,
    IReadOnlyList<string> Networks)
{
    public string ShortId => DockerFormat.ShortId(Id);

    /// <summary>The compose project the container belongs to (Docker Desktop groups them as a stack).</summary>
    public string? Project => Labels.GetValueOrDefault("com.docker.compose.project");

    public string? Service => Labels.GetValueOrDefault("com.docker.compose.service");

    /// <summary>The folder compose ran in, so a stack is known to be the open solution's.</summary>
    public string? WorkingDir => Labels.GetValueOrDefault("com.docker.compose.project.working_dir");

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public bool IsRunning => State == "running";

    public bool IsPaused => State == "paused";

    /// <summary>Running or paused: it can be stopped.</summary>
    public bool IsUp => State is "running" or "paused" or "restarting";

    /// <summary>The code it exited with, from "Exited (1) 2 minutes ago".</summary>
    public int? ExitCode => State == "exited" && ExitCodePattern().Match(Status) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var code) ? code : null;

    /// <summary>"healthy", "unhealthy" or "starting" when the image has a health check.</summary>
    public string? Health => HealthPattern().Match(Status) is { Success: true } m ? m.Groups[1].Value.Replace("health: ", "", StringComparison.Ordinal) : null;

    /// <summary>It stopped with an error, keeps restarting, or its health check fails.</summary>
    public bool IsFailing => ExitCode is not null and not 0 || State is "dead" or "restarting" || Health == "unhealthy";

    [GeneratedRegex(@"^Exited \((-?\d+)\)")]
    private static partial Regex ExitCodePattern();

    [GeneratedRegex(@"\((healthy|unhealthy|health: starting)\)")]
    private static partial Regex HealthPattern();
}

/// <summary>A local image, with how many containers use it (from <c>system df</c>).</summary>
public sealed record DockerImage(string Id, string Repository, string Tag, long Size, DateTimeOffset? Created, int Containers)
{
    public string ShortId => DockerFormat.ShortId(Id);

    public bool IsDangling => Repository == "<none>";

    /// <summary>"nginx:1.27", or the image ID for an untagged image (what <c>docker run</c> takes).</summary>
    public string Reference => IsDangling ? ShortId : Tag is "<none>" or "" ? Repository : $"{Repository}:{Tag}";

    public bool InUse => Containers > 0;
}

/// <summary>A volume, its size and how many containers mount it (from <c>system df</c>).</summary>
public sealed record DockerVolume(string Name, string Driver, string Mountpoint, long? Size, int Links, IReadOnlyDictionary<string, string> Labels)
{
    public DateTimeOffset? Created { get; init; }

    public bool InUse => Links > 0;

    /// <summary>An anonymous volume is named by a 64-character hex id.</summary>
    public bool IsAnonymous => Name.Length == 64 && Name.All(char.IsAsciiHexDigit);
}

public sealed record DockerNetwork(string Id, string Name, string Driver, string Scope, bool Internal, DateTimeOffset? Created, IReadOnlyDictionary<string, string> Labels)
{
    public string ShortId => DockerFormat.ShortId(Id);

    /// <summary>bridge, host and none come with the engine and cannot be removed.</summary>
    public bool IsBuiltIn => Name is "bridge" or "host" or "none";
}

/// <summary>One sample of <c>docker stats</c> for a container.</summary>
public sealed record DockerStats(string Id, double Cpu, long Memory, long MemoryLimit, long NetReceived, long NetSent, long BlockRead, long BlockWritten, int Pids);

/// <summary>A layer of an image (<c>docker history</c>).</summary>
public sealed record DockerLayer(string CreatedBy, long Size, string CreatedSince);

/// <summary>The engine: its version, the machine it runs on and what it may use.</summary>
public sealed record DockerInfo(string Version, string OperatingSystem, int Cpus, long Memory, string Name);

/// <summary>What <c>system df -v</c> reports: images and volumes with their use, and the disk each kind takes.</summary>
public sealed record DockerDiskUsage(IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerVolume> Volumes, long ContainersSize, long BuildCacheSize)
{
    public long ImagesSize => Images.Sum(i => i.Size);

    public long VolumesSize => Volumes.Sum(v => v.Size ?? 0);

    public long Total => ImagesSize + VolumesSize + ContainersSize + BuildCacheSize;
}

/// <summary>Reading what the CLI prints: sizes, dates, ports, labels.</summary>
public static partial class DockerFormat
{
    /// <summary>The 12 characters Docker shows of an id (without its "sha256:").</summary>
    public static string ShortId(string id)
    {
        var bare = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return bare.Length > 12 ? bare[..12] : bare;
    }

    /// <summary>"345MB", "20.5kB (virtual 379MB)", "22.7MiB": bytes, decimal or binary units as the CLI writes them.</summary>
    public static long? ParseSize(string? text)
    {
        if (text is null || SizePattern().Match(text) is not { Success: true } m)
            return null;
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "KIB" => 1024d,
            "MIB" => 1024d * 1024,
            "GIB" => 1024d * 1024 * 1024,
            "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)Math.Round(value * factor);
    }

    /// <summary>"22.7MiB / 30.44GiB": the two sizes of a stats column.</summary>
    public static (long First, long Second) ParsePair(string? text)
    {
        var parts = (text ?? "").Split('/', 2);
        return (ParseSize(parts[0]) ?? 0, parts.Length > 1 ? ParseSize(parts[1]) ?? 0 : 0);
    }

    /// <summary>Bytes as Docker Desktop shows them: "345.2 MB", "1.27 GB".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"2026-10-15 17:39:57 +0500 +05" (ps, images, networks) or an ISO time (inspect).</summary>
    public static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("0001-01-01", StringComparison.Ordinal))
            return null;
        if (CliTimePattern().Match(text) is { Success: true } m)
        {
            var offset = m.Groups[3].Value;
            var iso = $"{m.Groups[1].Value}T{m.Groups[2].Value}{offset[..3]}:{offset[3..]}";
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var cli) ? cli : null;
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    /// <summary>"6 days ago", as Docker Desktop's Last started and Created columns.</summary>
    public static string Ago(DateTimeOffset? time, DateTimeOffset? now = null)
    {
        if (time is not { } t)
            return "";
        var span = (now ?? DateTimeOffset.Now) - t;
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        return span.TotalSeconds < 60 ? "Just now"
            : span.TotalMinutes < 60 ? Unit((int)span.TotalMinutes, "minute")
            : span.TotalHours < 24 ? Unit((int)span.TotalHours, "hour")
            : span.TotalDays < 30 ? Unit((int)span.TotalDays, "day")
            : span.TotalDays < 365 ? Unit((int)(span.TotalDays / 30), "month")
            : Unit((int)(span.TotalDays / 365), "year");
    }

    /// <summary>
    /// The Ports column of <c>ps</c>: "0.0.0.0:5433->5432/tcp, [::]:5433->5432/tcp, 6379/tcp". The IPv4 and IPv6
    /// bindings of one port are one port; a range ("8000-8002->8000-8002/tcp") is each of its ports.
    /// </summary>
    public static IReadOnlyList<DockerPort> ParsePorts(string? text)
    {
        var ports = new List<DockerPort>();
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (PortPattern().Match(part) is not { Success: true } m)
                continue;
            var protocol = m.Groups[4].Value;
            var first = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var last = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : first;
            int? hostFirst = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            for (var i = 0; i <= Math.Min(last - first, 100); i++)
            {
                var port = new DockerPort(hostFirst + i, first + i, protocol);
                if (!ports.Contains(port))
                    ports.Add(port);
            }
        }
        // A port also published is not listed again as only exposed.
        return ports.Where(p => p.HostPort is not null || !ports.Any(o => o.HostPort is not null && o.ContainerPort == p.ContainerPort && o.Protocol == p.Protocol)).ToList();
    }

    /// <summary>"a=b,c=d" (docker) or an object (podman).</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString());
        var map = new Dictionary<string, string>();
        if (element.ValueKind != JsonValueKind.String)
            return map;
        foreach (var pair in (element.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (pair.IndexOf('=') is > 0 and var eq)
                map[pair[..eq]] = pair[(eq + 1)..];
        return map;
    }

    public static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : ""
            : "";

    public static int Int(JsonElement element, string property) =>
        int.TryParse(Str(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Terminal control sequences (the stats stream clears the screen between samples).</summary>
    public static string StripEscapes(string text) => EscapePattern().Replace(text, "");

    [GeneratedRegex(@"^\s*([\d.]+)\s*([KMGT]i?B|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2})(?:\.\d+)? ([+-]\d{4})")]
    private static partial Regex CliTimePattern();

    [GeneratedRegex(@"^(?:\S*:(\d+)(?:-\d+)?->)?(\d+)(?:-(\d+))?/(\w+)$")]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex EscapePattern();
}
