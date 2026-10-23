using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.Lsp;

namespace Karvon.Debugging;

public sealed record StackFrameInfo(int Id, string Name, string? SourcePath, int Line, int Column);

public sealed record ScopeInfo(string Name, int VariablesReference, bool Expensive);

public sealed record VariableInfo(string Name, string Value, string? Type, int VariablesReference);

public sealed record BreakpointResult(bool Verified, int? Line, string? Message);

public sealed record EvaluateResult(string Value, string? Type, int VariablesReference);

public sealed record LaunchRequest(
    string Program,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// One debugging session (launch, breakpoints, stepping, inspection) with a debug adapter: netcoredbg for .NET, a module's
/// for its runtime. An adapter that debugs several processes (js-debug: npm, then the node it starts) asks for a child
/// session for each (startDebugging); the breakpoints go to all of them, and stepping and inspection to the one that
/// stopped last.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    readonly DebugAdapter _adapter;
    readonly DapConnection _root;
    readonly List<DapConnection> _children = [];
    readonly Dictionary<string, IReadOnlyList<int>> _breakpoints = [];
    readonly Dictionary<(DapConnection, int), (string File, int Line)> _breakpointIds = [];
    DapConnection? _stopped;
    int _ended;
    int? _exitCode;

    DebugSession(DebugAdapter adapter, DapConnection root)
    {
        _adapter = adapter;
        _root = root;
        Watch(root);
        root.Disconnected += _ => EndAfterChildren();
    }

    /// <summary>The debuggee stopped (breakpoint, step, exception, pause): thread id, reason, description.</summary>
    public event Action<int, string, string?>? Stopped;

    public event Action? Continued;

    /// <summary>Program or adapter output: category ("stdout", "stderr", "console"), text.</summary>
    public event Action<string, string>? Output;

    /// <summary>The session is over; carries the exit code when the adapter reported one.</summary>
    public event Action<int?>? Ended;

    /// <summary>A breakpoint became bound (or unbound) later, e.g. when its module loaded: file, requested line, verified.</summary>
    public event Action<string, int, bool>? BreakpointChanged;

    /// <summary>Launches a .NET program under netcoredbg.</summary>
    public static Task<DebugSession> LaunchAsync(
        string adapterPath,
        LaunchRequest launch,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct) =>
        StartAsync(Netcoredbg(adapterPath), "launch", Json(new
        {
            name = "Karvon Launch",
            type = "coreclr",
            request = "launch",
            program = launch.Program,
            args = launch.Args,
            cwd = launch.WorkingDirectory,
            env = launch.Environment,
            stopAtEntry = false,
            justMyCode = true,
            enableStepFiltering = true,
            console = "internalConsole",
        }), breakpoints, onBreakpointsVerified, beforeConfigurationDone, ct);

    /// <summary>Attaches to a running .NET process (used to debug tests: vstest's testhost waits for us).</summary>
    public static Task<DebugSession> AttachAsync(
        string adapterPath,
        int processId,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct) =>
        StartAsync(Netcoredbg(adapterPath), "attach", Json(new
        {
            name = "Karvon Attach",
            type = "coreclr",
            request = "attach",
            processId,
            justMyCode = true,
            enableStepFiltering = true,
        }), breakpoints, onBreakpointsVerified, beforeConfigurationDone, ct);

    /// <summary>netcoredbg, on its stdin and stdout.</summary>
    public static DebugAdapter Netcoredbg(string adapterPath)
    {
        var psi = new ProcessStartInfo(adapterPath);
        psi.ArgumentList.Add("--interpreter=vscode");
        DotnetCli.ApplyEnvironment(psi);
        return DebugAdapter.Start("coreclr", psi);
    }

    static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), JsonRpcConnection.JsonOptions)!.AsObject();

    /// <summary>
    /// A session with an adapter (which it owns from now on): its launch or attach request with the configuration
    /// (launch.json's), the breakpoints set before the program runs. Returns once the adapter has started it.
    /// </summary>
    public static async Task<DebugSession> StartAsync(
        DebugAdapter adapter,
        string request,
        JsonObject configuration,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct)
    {
        DapConnection root;
        try
        {
            root = await adapter.ConnectAsync(ct);
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
        var session = new DebugSession(adapter, root);
        foreach (var (file, lines) in breakpoints)
            session._breakpoints[file] = lines;
        beforeConfigurationDone?.Invoke(session);
        adapter.StartLogging(line => session.Output?.Invoke("console", line + "\n"));
        try
        {
            await InitializeAsync(root, adapter.Type, request, configuration, async () =>
            {
                foreach (var (file, lines) in breakpoints)
                    onBreakpointsVerified(file, await session.SetBreakpointsAsync(root, file, lines, ct));
            }, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// DAP's start of a session: initialize, the launch or attach request, the breakpoints once the adapter is
    /// initialized, configurationDone; and the answer to launch, which some adapters give only after configurationDone.
    /// </summary>
    static async Task InitializeAsync(DapConnection dap, string adapterId, string request, object configuration, Func<Task> setBreakpoints, CancellationToken ct)
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dap.EventReceived += (name, _) =>
        {
            if (name == "initialized")
                initialized.TrySetResult();
        };
        dap.Start();
        await dap.RequestAsync("initialize", new
        {
            clientID = "karvon",
            clientName = "Karvon",
            adapterID = adapterId,
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            supportsVariableType = true,
            supportsRunInTerminalRequest = false,
            supportsStartDebuggingRequest = true,
            locale = "en-us",
        }, ct);

        var startTask = dap.RequestAsync(request, configuration, ct);
        await Task.WhenAny(initialized.Task, startTask).WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (startTask.IsFaulted)
            await startTask;
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await setBreakpoints();
        await dap.RequestAsync("configurationDone", null, ct);
        await startTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>A connection's events and requests, as the session's.</summary>
    void Watch(DapConnection dap)
    {
        dap.EventReceived += (name, body) => OnEvent(dap, name, body);
        dap.ReverseRequest = (command, arguments) => command switch
        {
            "startDebugging" => StartChild(arguments),
            _ => throw new NotSupportedException("not supported by Karvon"),
        };
    }

    /// <summary>
    /// The adapter asks for a child session (a process it attached to): it is answered at once, and the child starts on a
    /// connection of its own with the configuration it gave, the breakpoints set.
    /// </summary>
    Task<object?> StartChild(JsonElement arguments)
    {
        var request = arguments.TryGetProperty("request", out var r) ? r.GetString() ?? "launch" : "launch";
        var configuration = arguments.GetProperty("configuration").Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                var child = await _adapter.ConnectAsync(CancellationToken.None);
                Watch(child);
                lock (_children)
                    _children.Add(child);
                child.Disconnected += _ => RemoveChild(child);
                await InitializeAsync(child, _adapter.Type, request, configuration, async () =>
                {
                    foreach (var (file, lines) in Breakpoints())
                    {
                        var results = await SetBreakpointsAsync(child, file, lines, CancellationToken.None);
                        foreach (var (line, result) in lines.Zip(results))
                            BreakpointChanged?.Invoke(file, line, result.Verified);
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or SocketException or DapException or TimeoutException or OperationCanceledException or NotSupportedException)
            {
                Output?.Invoke("console", $"A child session did not start: {ex.Message}\n");
            }
        });
        return Task.FromResult<object?>(null);
    }

    void RemoveChild(DapConnection child)
    {
        lock (_children)
            _children.Remove(child);
        if (_stopped == child)
            _stopped = null;
    }

    List<KeyValuePair<string, IReadOnlyList<int>>> Breakpoints()
    {
        lock (_breakpoints)
            return _breakpoints.ToList();
    }

    /// <summary>Where the program runs: its child sessions when it has any (the root of js-debug's runs none), else the session.</summary>
    List<DapConnection> Debuggees()
    {
        lock (_children)
            return _children.Count > 0 ? [.. _children] : [_root];
    }

    /// <summary>Where stepping and inspection go: the session that stopped last, else the newest child, else the root.</summary>
    DapConnection Focus
    {
        get
        {
            if (_stopped is { } stopped)
                return stopped;
            lock (_children)
                return _children.Count > 0 ? _children[^1] : _root;
        }
    }

    void OnEvent(DapConnection dap, string name, JsonElement body)
    {
        switch (name)
        {
            case "stopped":
                _stopped = dap;
                var thread = body.TryGetProperty("threadId", out var t) ? t.GetInt32() : 0;
                var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                // text is what more there is to say (the exception); description says the reason again in words
                // ("Paused on breakpoint"), which is worth it for an exception only.
                var text = body.TryGetProperty("text", out var tx) ? tx.GetString()
                    : reason == "exception" && body.TryGetProperty("description", out var d) ? d.GetString() : null;
                Stopped?.Invoke(thread, reason, text);
                break;
            case "continued":
                Continued?.Invoke();
                break;
            case "output":
                var category = body.TryGetProperty("category", out var c) ? c.GetString() ?? "console" : "console";
                // js-debug's telemetry is no output of the program.
                if (category != "telemetry" && body.TryGetProperty("output", out var o))
                    Output?.Invoke(category, o.GetString() ?? "");
                break;
            case "exited":
                if (body.TryGetProperty("exitCode", out var code))
                    _exitCode = code.GetInt32();
                break;
            case "terminated":
                if (dap == _root)
                    EndAfterChildren();
                else
                    RemoveChild(dap);
                break;
            case "breakpoint":
                if (body.TryGetProperty("breakpoint", out var bp) && bp.TryGetProperty("id", out var id))
                {
                    (string File, int Line) location;
                    lock (_breakpointIds)
                        if (!_breakpointIds.TryGetValue((dap, id.GetInt32()), out location))
                            break;
                    BreakpointChanged?.Invoke(location.File, location.Line, bp.TryGetProperty("verified", out var ok) && ok.GetBoolean());
                }
                break;
        }
    }

    /// <summary>
    /// The session is over once its children are (each says so on its own connection, after its last output, which
    /// may come after the session's own end on another): it waits for them a little, then ends.
    /// </summary>
    void EndAfterChildren() => _ = Task.Run(async () =>
    {
        for (var i = 0; i < 40 && ChildCount() > 0; i++)
            await Task.Delay(50);
        End(_exitCode);
    });

    int ChildCount()
    {
        lock (_children)
            return _children.Count;
    }

    void End(int? exitCode)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            Ended?.Invoke(exitCode ?? _exitCode);
    }

    public bool HasEnded => _ended == 1;

    /// <summary>A file's breakpoints now (all of them: the lines replace those it had), in each process debugged.</summary>
    public async Task<IReadOnlyList<BreakpointResult>> SetBreakpointsAsync(string file, IReadOnlyList<int> lines, CancellationToken ct = default)
    {
        lock (_breakpoints)
            _breakpoints[file] = lines;
        IReadOnlyList<BreakpointResult>? first = null;
        foreach (var dap in Debuggees())
        {
            var results = await SetBreakpointsAsync(dap, file, lines, ct);
            first ??= results;
        }
        return first ?? [];
    }

    async Task<IReadOnlyList<BreakpointResult>> SetBreakpointsAsync(DapConnection dap, string file, IReadOnlyList<int> lines, CancellationToken ct)
    {
        var body = await dap.RequestAsync("setBreakpoints", new
        {
            source = new { path = file, name = Path.GetFileName(file) },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
            lines,
            sourceModified = false,
        }, ct);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("breakpoints", out var list))
            return lines.Select(_ => new BreakpointResult(false, null, null)).ToList();
        lock (_breakpointIds)
        {
            foreach (var key in _breakpointIds.Where(kv => kv.Key.Item1 == dap && kv.Value.File == file).Select(kv => kv.Key).ToList())
                _breakpointIds.Remove(key);
            var i = 0;
            foreach (var b in list.EnumerateArray())
            {
                if (b.TryGetProperty("id", out var id) && i < lines.Count)
                    _breakpointIds[(dap, id.GetInt32())] = (file, lines[i]);
                i++;
            }
        }
        return list.EnumerateArray().Select(b => new BreakpointResult(
            b.TryGetProperty("verified", out var v) && v.GetBoolean(),
            b.TryGetProperty("line", out var l) ? l.GetInt32() : null,
            b.TryGetProperty("message", out var m) ? m.GetString() : null)).ToList();
    }

    public Task ContinueAsync(int threadId) => RunAsync("continue", threadId);

    public Task StepOverAsync(int threadId) => RunAsync("next", threadId);

    public Task StepInAsync(int threadId) => RunAsync("stepIn", threadId);

    public Task StepOutAsync(int threadId) => RunAsync("stepOut", threadId);

    /// <summary>
    /// The program goes on. A child's process that runs to its end at once may close its connection before the adapter
    /// answers (js-debug's node): that child is done, which is no error.
    /// </summary>
    async Task RunAsync(string command, int threadId)
    {
        var dap = Focus;
        try
        {
            await dap.RequestAsync(command, new { threadId });
        }
        catch (IOException) when (dap != _root)
        {
            RemoveChild(dap);
        }
    }

    public async Task PauseAsync()
    {
        var dap = Focus;
        var threads = await dap.RequestAsync("threads", null);
        var id = threads.TryGetProperty("threads", out var list) && list.GetArrayLength() > 0 ? list[0].GetProperty("id").GetInt32() : 0;
        await dap.RequestAsync("pause", new { threadId = id });
    }

    public async Task<IReadOnlyList<StackFrameInfo>> StackTraceAsync(int threadId, CancellationToken ct = default)
    {
        var body = await Focus.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 100 }, ct);
        if (!body.TryGetProperty("stackFrames", out var frames))
            return [];
        return frames.EnumerateArray().Select(f =>
        {
            string? path = null;
            if (f.TryGetProperty("source", out var s) && s.TryGetProperty("path", out var p))
                path = p.GetString();
            return new StackFrameInfo(f.GetProperty("id").GetInt32(), f.GetProperty("name").GetString() ?? "",
                path, f.TryGetProperty("line", out var l) ? l.GetInt32() : 0, f.TryGetProperty("column", out var c) ? c.GetInt32() : 0);
        }).ToList();
    }

    public async Task<IReadOnlyList<ScopeInfo>> ScopesAsync(int frameId, CancellationToken ct = default)
    {
        var body = await Focus.RequestAsync("scopes", new { frameId }, ct);
        if (!body.TryGetProperty("scopes", out var scopes))
            return [];
        return scopes.EnumerateArray().Select(s => new ScopeInfo(
            s.GetProperty("name").GetString() ?? "",
            s.GetProperty("variablesReference").GetInt32(),
            s.TryGetProperty("expensive", out var e) && e.GetBoolean())).ToList();
    }

    public async Task<IReadOnlyList<VariableInfo>> VariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        var body = await Focus.RequestAsync("variables", new { variablesReference }, ct);
        if (!body.TryGetProperty("variables", out var vars))
            return [];
        return vars.EnumerateArray().Select(v => new VariableInfo(
            v.GetProperty("name").GetString() ?? "",
            v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "",
            v.TryGetProperty("type", out var t) ? t.GetString() : null,
            v.TryGetProperty("variablesReference", out var r) ? r.GetInt32() : 0)).ToList();
    }

    /// <summary>Evaluates an expression in a frame; context is "watch", "hover" or "repl".</summary>
    public async Task<EvaluateResult> EvaluateAsync(string expression, int? frameId, string context, CancellationToken ct = default)
    {
        var body = await Focus.RequestAsync("evaluate", new { expression, frameId, context }, ct);
        return new EvaluateResult(
            body.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            body.TryGetProperty("type", out var t) ? t.GetString() : null,
            body.TryGetProperty("variablesReference", out var v) ? v.GetInt32() : 0);
    }

    public async Task TerminateAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _root.RequestAsync("disconnect", new { terminateDebuggee = true }, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or DapException)
        {
        }
        End(_exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (!HasEnded && !_adapter.HasExited)
            await TerminateAsync();
        List<DapConnection> children;
        lock (_children)
            children = [.. _children];
        foreach (var child in children)
            await child.DisposeAsync();
        await _root.DisposeAsync();
        await _adapter.DisposeAsync();
    }
}
