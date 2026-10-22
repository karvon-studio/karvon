using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Karvon.Docker;

/// <summary>Why the engine could not do something: not installed, not running, no access to its socket, or its own error.</summary>
public enum DockerProblem
{
    Failed,
    NotInstalled,
    NotRunning,
    PermissionDenied,
}

public sealed class DockerException(string message, DockerProblem problem = DockerProblem.Failed) : Exception(message)
{
    public DockerProblem Problem { get; } = problem;
}

/// <summary>What a command printed, and how it ended.</summary>
public sealed record DockerResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// The container engine through its command line, as Docker Desktop's own CLI: <c>docker</c>, or <c>podman</c> when
/// Docker is not installed (<c>KARVON_DOCKER_PATH</c> names another). Lists are read as JSON lines; events, stats and
/// logs are long-running commands whose lines arrive as they are printed (<see cref="Stream"/>).
/// </summary>
public sealed class DockerEngine(string program)
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public string Program { get; } = program;

    /// <summary>KARVON_DOCKER_PATH, else docker on the PATH, else podman, else "docker" (which reports it is missing).</summary>
    public static DockerEngine Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_DOCKER_PATH") is { Length: > 0 } path)
            return new DockerEngine(path);
        return new DockerEngine(OnPath("docker") ? "docker" : OnPath("podman") ? "podman" : "docker");
    }

    static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, name)) || File.Exists(Path.Combine(folder, name + ".exe")));

    public string Name => Path.GetFileName(Program);

    // ---- Running ------------------------------------------------------------------------------------------------

    /// <summary>Runs a command to its end; <paramref name="onLine"/> gets its output as it comes (pull, compose up).</summary>
    public async Task<DockerResult> RunAsync(IEnumerable<string> args, string? folder = null, TimeSpan? timeout = null,
        Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = StartInfo(args, folder) };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DockerException($"{Name} is not installed or not on the PATH ({ex.Message}).", DockerProblem.NotInstalled);
        }
        var output = new StringBuilder();
        var error = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder into)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lock (into)
                    into.Append(line).Append('\n');
                onLine?.Invoke(line);
            }
        }
        var reading = Task.WhenAll(Read(process.StandardOutput, output), Read(process.StandardError, error));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await reading;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new DockerException($"{Name} {string.Join(' ', process.StartInfo.ArgumentList)} did not finish in {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
        return new DockerResult(process.ExitCode, output.ToString().TrimEnd(), error.ToString().TrimEnd());
    }

    /// <summary>Runs and returns the output, or throws with the engine's own words (and what they mean).</summary>
    public async Task<string> OutputAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(args, cancellationToken: cancellationToken);
        if (!result.Succeeded)
            throw Failure(result.Message);
        return result.Output;
    }

    /// <summary>The engine's error, told apart: the daemon is not running, or the user may not use its socket.</summary>
    public DockerException Failure(string message)
    {
        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) && message.Contains(".sock", StringComparison.Ordinal))
            return new DockerException(message, DockerProblem.PermissionDenied);
        if (message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed to connect to the docker API", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error during connect", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unable to connect to Podman", StringComparison.OrdinalIgnoreCase))
            return new DockerException(message, DockerProblem.NotRunning);
        return new DockerException(message);
    }

    /// <summary>
    /// A command that runs until stopped (events, stats, logs -f, docker exec): each line of its output and error is
    /// given to <paramref name="onLine"/> on a background thread; <paramref name="onExit"/> gets its exit code.
    /// </summary>
    public DockerStream Stream(IEnumerable<string> args, Action<string> onLine, Action<int>? onExit = null)
    {
        try
        {
            return new DockerStream(StartInfo(args, null), onLine, onExit);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DockerException($"{Name} is not installed or not on the PATH ({ex.Message}).", DockerProblem.NotInstalled);
        }
    }

    ProcessStartInfo StartInfo(IEnumerable<string> args, string? folder)
    {
        var info = new ProcessStartInfo(Program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder is not null && Directory.Exists(folder) ? folder : "",
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    // ---- Reading ------------------------------------------------------------------------------------------------

    public async Task<DockerInfo> InfoAsync(CancellationToken cancellationToken = default)
    {
        var output = await OutputAsync(["info", "--format", "{{json .}}"], cancellationToken);
        using var json = JsonDocument.Parse(output);
        var info = json.RootElement;
        // docker info prints what it could not reach (the daemon) in ServerErrors, and still exits 0.
        if (info.TryGetProperty("ServerErrors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw Failure(string.Join("\n", errors.EnumerateArray().Select(e => e.GetString())));
        return new DockerInfo(DockerFormat.Str(info, "ServerVersion"), DockerFormat.Str(info, "OperatingSystem"), DockerFormat.Int(info, "NCPU"),
            info.TryGetProperty("MemTotal", out var memory) && memory.TryGetInt64(out var bytes) ? bytes : 0, DockerFormat.Str(info, "Name"));
    }

    /// <summary>Every container, with when it last started (one <c>inspect</c> for all of them).</summary>
    public async Task<IReadOnlyList<DockerContainer>> ContainersAsync(CancellationToken cancellationToken = default)
    {
        var output = await OutputAsync(["ps", "-a", "--no-trunc", "--format", "{{json .}}"], cancellationToken);
        var containers = new List<DockerContainer>();
        foreach (var line in Lines(output))
        {
            using var json = JsonDocument.Parse(line);
            var c = json.RootElement;
            containers.Add(new DockerContainer(DockerFormat.Str(c, "ID"), DockerFormat.Str(c, "Names").Split(',')[0], DockerFormat.Str(c, "Image"),
                DockerFormat.Str(c, "State").ToLowerInvariant(), DockerFormat.Str(c, "Status"),
                c.TryGetProperty("Labels", out var labels) ? DockerFormat.ParseLabels(labels) : new Dictionary<string, string>(),
                DockerFormat.ParsePorts(DockerFormat.Str(c, "Ports")), DockerFormat.Str(c, "Command").Trim('"'), DockerFormat.ParseTime(DockerFormat.Str(c, "CreatedAt")),
                Split(DockerFormat.Str(c, "Mounts")), Split(DockerFormat.Str(c, "Networks"))));
        }
        if (containers.Count == 0)
            return containers;
        try
        {
            var times = await OutputAsync(["container", "inspect", "--format", "{{.Id}} {{.State.StartedAt}} {{.State.FinishedAt}}", .. containers.Select(c => c.Id)], cancellationToken);
            var started = Lines(times).Select(l => l.Split(' ')).Where(p => p.Length >= 3).ToDictionary(p => p[0], p => (DockerFormat.ParseTime(p[1]), DockerFormat.ParseTime(p[2])));
            return [.. containers.Select(c => started.TryGetValue(c.Id, out var t) ? c with { StartedAt = t.Item1, FinishedAt = t.Item2 } : c)];
        }
        catch (DockerException)
        {
            // One went away between the two commands: the list stands without its times.
            return containers;
        }
    }

    /// <summary>Images and volumes with their sizes and use, in one call (Docker Desktop's numbers).</summary>
    public async Task<DockerDiskUsage> DiskUsageAsync(CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(await OutputAsync(["system", "df", "-v", "--format", "{{json .}}"], cancellationToken));
        var root = json.RootElement;
        IEnumerable<JsonElement> Items(string name) => root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
        var images = Items("Images").Select(i => new DockerImage(DockerFormat.Str(i, "ID"), DockerFormat.Str(i, "Repository"), DockerFormat.Str(i, "Tag"),
            DockerFormat.ParseSize(DockerFormat.Str(i, "Size")) ?? 0, DockerFormat.ParseTime(DockerFormat.Str(i, "CreatedAt")), DockerFormat.Int(i, "Containers"))).ToList();
        var volumes = Items("Volumes").Select(v => new DockerVolume(DockerFormat.Str(v, "Name"), DockerFormat.Str(v, "Driver"), DockerFormat.Str(v, "Mountpoint"),
            DockerFormat.ParseSize(DockerFormat.Str(v, "Size")), DockerFormat.Int(v, "Links"),
            v.TryGetProperty("Labels", out var labels) ? DockerFormat.ParseLabels(labels) : new Dictionary<string, string>())).ToList();
        var containers = Items("Containers").Sum(c => DockerFormat.ParseSize(DockerFormat.Str(c, "Size")) ?? 0);
        var cache = Items("BuildCache").Sum(b => DockerFormat.ParseSize(DockerFormat.Str(b, "Size")) ?? 0);
        if (volumes.Count > 0)
        {
            try
            {
                var created = await OutputAsync(["volume", "inspect", "--format", "{{.Name}} {{.CreatedAt}}", .. volumes.Select(v => v.Name)], cancellationToken);
                var times = Lines(created).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => DockerFormat.ParseTime(p[1]));
                volumes = [.. volumes.Select(v => times.TryGetValue(v.Name, out var t) ? v with { Created = t } : v)];
            }
            catch (DockerException)
            {
            }
        }
        return new DockerDiskUsage(images, volumes, containers, cache);
    }

    public async Task<IReadOnlyList<DockerNetwork>> NetworksAsync(CancellationToken cancellationToken = default) =>
        [.. Lines(await OutputAsync(["network", "ls", "--no-trunc", "--format", "{{json .}}"], cancellationToken)).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var n = json.RootElement;
            return new DockerNetwork(DockerFormat.Str(n, "ID"), DockerFormat.Str(n, "Name"), DockerFormat.Str(n, "Driver"), DockerFormat.Str(n, "Scope"),
                DockerFormat.Str(n, "Internal") == "true", DockerFormat.ParseTime(DockerFormat.Str(n, "CreatedAt")),
                n.TryGetProperty("Labels", out var labels) ? DockerFormat.ParseLabels(labels) : new Dictionary<string, string>());
        })];

    /// <summary>The raw <c>inspect</c> of a container, image, volume or network.</summary>
    public async Task<JsonElement> InspectAsync(string type, string id, CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(await OutputAsync([type, "inspect", id], cancellationToken));
        return json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0 ? json.RootElement[0].Clone() : json.RootElement.Clone();
    }

    /// <summary>An image's layers, newest first.</summary>
    public async Task<IReadOnlyList<DockerLayer>> HistoryAsync(string image, CancellationToken cancellationToken = default) =>
        [.. Lines(await OutputAsync(["image", "history", "--no-trunc", "--format", "{{json .}}", image], cancellationToken)).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var l = json.RootElement;
            return new DockerLayer(DockerFormat.Str(l, "CreatedBy"), DockerFormat.ParseSize(DockerFormat.Str(l, "Size")) ?? 0, DockerFormat.Str(l, "CreatedSince"));
        })];

    /// <summary>One line of <c>docker stats</c> (the stream's or --no-stream's), or null for anything else.</summary>
    public static DockerStats? ParseStats(string line)
    {
        line = DockerFormat.StripEscapes(line).Trim();
        if (!line.StartsWith('{'))
            return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var s = json.RootElement;
            var (memory, limit) = DockerFormat.ParsePair(DockerFormat.Str(s, "MemUsage"));
            var (received, sent) = DockerFormat.ParsePair(DockerFormat.Str(s, "NetIO"));
            var (read, written) = DockerFormat.ParsePair(DockerFormat.Str(s, "BlockIO"));
            var cpu = double.TryParse(DockerFormat.Str(s, "CPUPerc").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;
            var id = DockerFormat.Str(s, "ID") is { Length: > 0 } shortId ? shortId : DockerFormat.Str(s, "Container");
            return id.Length == 0 ? null : new DockerStats(id, cpu, memory, limit, received, sent, read, written, DockerFormat.Int(s, "PIDs"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Changing -----------------------------------------------------------------------------------------------

    /// <summary>start, stop, restart, pause, unpause or rm (forced) for one or more containers.</summary>
    public Task<DockerResult> ContainersAsync(string verb, IEnumerable<string> ids) =>
        RunAsync(verb == "rm" ? ["rm", "-f", .. ids] : [verb, .. ids], timeout: TimeSpan.FromMinutes(2));

    public Task<DockerResult> RemoveImagesAsync(IEnumerable<string> ids) => RunAsync(["rmi", .. ids], timeout: TimeSpan.FromMinutes(2));

    public Task<DockerResult> RemoveVolumesAsync(IEnumerable<string> names) => RunAsync(["volume", "rm", .. names]);

    public Task<DockerResult> RemoveNetworksAsync(IEnumerable<string> ids) => RunAsync(["network", "rm", .. ids]);

    public Task<DockerResult> CreateVolumeAsync(string name) => RunAsync(["volume", "create", name]);

    public Task<DockerResult> CreateNetworkAsync(string name, string driver) => RunAsync(["network", "create", "--driver", driver, name]);

    /// <summary>Removes every image no container uses (Docker Desktop's Clean up).</summary>
    public Task<DockerResult> PruneImagesAsync() => RunAsync(["image", "prune", "-a", "-f"], timeout: TimeSpan.FromMinutes(5));

    public Task<DockerResult> PullAsync(string image, Action<string>? onLine = null, CancellationToken cancellationToken = default) =>
        RunAsync(["pull", image], timeout: TimeSpan.FromMinutes(30), onLine: onLine, cancellationToken: cancellationToken);

    /// <summary><c>docker run -d</c> with Docker Desktop's optional settings; the output is the new container's id.</summary>
    public Task<DockerResult> RunContainerAsync(string image, string? name, IEnumerable<(string Host, string Container)> ports,
        IEnumerable<(string Host, string Container)> volumes, IEnumerable<(string Name, string Value)> environment)
    {
        List<string> args = ["run", "-d"];
        if (!string.IsNullOrWhiteSpace(name))
            args.AddRange(["--name", name.Trim()]);
        foreach (var (host, container) in ports)
            args.AddRange(["-p", $"{host}:{container}"]);
        foreach (var (host, container) in volumes)
            args.AddRange(["-v", $"{host}:{container}"]);
        foreach (var (key, value) in environment)
            args.AddRange(["-e", $"{key}={value}"]);
        args.Add(image);
        return RunAsync(args, timeout: TimeSpan.FromMinutes(30));
    }

    /// <summary><c>docker compose</c> in a folder (up -d, down...), its output line by line.</summary>
    public Task<DockerResult> ComposeAsync(string folder, IEnumerable<string> args, Action<string>? onLine = null) =>
        RunAsync(["compose", .. args], folder, TimeSpan.FromMinutes(30), onLine);

    static IEnumerable<string> Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static IReadOnlyList<string> Split(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>A long-running engine command (see <see cref="DockerEngine.Stream"/>); disposing it ends the command.</summary>
public sealed class DockerStream : IDisposable
{
    readonly Process _process = new();
    int _disposed;

    internal DockerStream(ProcessStartInfo info, Action<string> onLine, Action<int>? onExit)
    {
        _process.StartInfo = info;
        _process.Start();
        async Task Read(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    if (Volatile.Read(ref _disposed) == 0)
                        onLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(Read(_process.StandardOutput), Read(_process.StandardError));
            int code;
            try
            {
                await _process.WaitForExitAsync();
                code = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }
            if (Volatile.Read(ref _disposed) == 0)
                onExit?.Invoke(code);
        });
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        DockerEngine.Kill(_process);
        _process.Dispose();
    }
}
