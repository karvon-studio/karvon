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

/// <summary>An exception filter a debugger offers (DAP's exceptionBreakpointFilters): netcoredbg's "all" and "user-unhandled".</summary>
public sealed record ExceptionFilter(string Filter, string Label, bool Default, string? Description = null);

/// <summary>What a debugger says it can do: conditions, hit counts and log messages on breakpoints, the exceptions it breaks on.</summary>
public sealed record DebugCapabilities(bool ConditionalBreakpoints, bool HitConditionalBreakpoints, bool LogPoints, IReadOnlyList<ExceptionFilter> ExceptionFilters)
{
    public static readonly DebugCapabilities None = new(false, false, false, []);

    public static DebugCapabilities Of(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return None;
        bool Has(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var filters = body.TryGetProperty("exceptionBreakpointFilters", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(f => new ExceptionFilter(f.GetProperty("filter").GetString() ?? "",
                f.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "", f.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True,
                f.TryGetProperty("description", out var desc) ? desc.GetString() : null)).Where(f => f.Filter.Length > 0).ToList()
            : [];
        return new DebugCapabilities(Has("supportsConditionalBreakpoints"), Has("supportsHitConditionalBreakpoints"), Has("supportsLogPoints"), filters);
    }
}

public sealed record EvaluateResult(string Value, string? Type, int VariablesReference);

/// <summary>
/// One debugging session (launch, breakpoints, stepping, inspection) with a debug adapter, a module's
/// for its runtime. An adapter that debugs several processes (js-debug: npm, then the node it starts) asks for a child
/// session for each (startDebugging); the breakpoints go to all of them, and stepping and inspection to the one that
/// stopped last.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    readonly DebugAdapter _adapter;
    readonly DapConnection _root;
    readonly List<DapConnection> _children = [];
    /// <summary>What the adapter asked to run in a terminal (runInTerminal): run here, its output the program's.</summary>
    readonly List<ProcessRunner> _terminals = [];
    readonly Dictionary<string, IReadOnlyList<SourceBreakpoint>> _breakpoints = [];
    IReadOnlyList<string> _exceptionFilters = [];

    /// <summary>What the debugger said it can do, when it started.</summary>
    public DebugCapabilities Capabilities { get; private set; } = DebugCapabilities.None;
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

    static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), JsonRpcConnection.JsonOptions)!.AsObject();

    /// <summary>
    /// A session with an adapter (which it owns from now on): its launch or attach request with the configuration
    /// (launch.json's), the breakpoints set before the program runs. Returns once the adapter has started it.
    /// </summary>
    public static async Task<DebugSession> StartAsync(
        DebugAdapter adapter,
        string request,
        JsonObject configuration,
        IReadOnlyDictionary<string, IReadOnlyList<SourceBreakpoint>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct,
        Func<DebugCapabilities, IReadOnlyList<string>>? exceptionFilters = null)
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
            await InitializeAsync(root, adapter.Type, request, configuration, async capabilities =>
            {
                session.Capabilities = capabilities;
                foreach (var (file, lines) in breakpoints)
                    onBreakpointsVerified(file, await session.SetBreakpointsAsync(root, file, lines, ct));
                // The exceptions it breaks on, as the user chose them for this debugger (else its defaults).
                session._exceptionFilters = exceptionFilters?.Invoke(capabilities) ?? [.. capabilities.ExceptionFilters.Where(f => f.Default).Select(f => f.Filter)];
                if (capabilities.ExceptionFilters.Count > 0)
                    await SetExceptionBreakpointsAsync(root, session._exceptionFilters, ct);
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
    static async Task InitializeAsync(DapConnection dap, string adapterId, string request, object configuration, Func<DebugCapabilities, Task> setBreakpoints, CancellationToken ct)
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dap.EventReceived += (name, _) =>
        {
            if (name == "initialized")
                initialized.TrySetResult();
        };
        dap.Start();
        var capabilities = await dap.RequestAsync("initialize", new
        {
            clientID = "karvon",
            clientName = "Karvon",
            adapterID = adapterId,
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            supportsVariableType = true,
            supportsRunInTerminalRequest = true,
            supportsStartDebuggingRequest = true,
            locale = "en-us",
        }, ct);

        var startTask = dap.RequestAsync(request, configuration, ct);
        await Task.WhenAny(initialized.Task, startTask).WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (startTask.IsFaulted)
            await startTask;
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await setBreakpoints(DebugCapabilities.Of(capabilities));
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
            "runInTerminal" => RunInTerminal(arguments),
            _ => throw new NotSupportedException("not supported by Karvon"),
        };
    }

    /// <summary>
    /// The adapter asks for its command to run in a terminal (a console of integratedTerminal, externalTerminal: kotlin-lsp's
    /// Gradle, which runs the program): it runs here, its output in the session's (as the terminal would show it), and the
    /// session lasts until it is done, the adapter's end aside (Gradle writes a task's output when the task is over).
    /// </summary>
    Task<object?> RunInTerminal(JsonElement arguments)
    {
        var args = arguments.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToList();
        if (args.Count == 0)
            throw new ArgumentException("runInTerminal has no command.");
        var cwd = arguments.TryGetProperty("cwd", out var c) && c.GetString() is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;
        Dictionary<string, string> environment = [];
        HashSet<string> unset = [];
        if (arguments.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            foreach (var variable in env.EnumerateObject())
                if (variable.Value.ValueKind == JsonValueKind.String)
                    environment[variable.Name] = variable.Value.GetString()!;
                else
                    unset.Add(variable.Name);
        // A shell's line when the adapter says the shell may read it (its quoting, its variables), else the program's.
        var (program, programArgs) = arguments.TryGetProperty("argsCanBeInterpretedByShell", out var shell) && shell.ValueKind == JsonValueKind.True
            ? Workspace.TaskVariables.Shell(string.Join(' ', args))
            : (args[0], args[1..]);
        var runner = ProcessRunner.Start(program, programArgs, cwd, line => Output?.Invoke("stdout", line + "\n"), environment, unset);
        lock (_terminals)
            _terminals.Add(runner);
        _ = Task.Run(async () =>
        {
            await runner.WaitAsync();
            lock (_terminals)
                _terminals.Remove(runner);
            runner.Dispose();
        });
        return Task.FromResult<object?>(new { processId = runner.Id });
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
                await InitializeAsync(child, _adapter.Type, request, configuration, async capabilities =>
                {
                    foreach (var (file, lines) in Breakpoints())
                    {
                        var results = await SetBreakpointsAsync(child, file, lines, CancellationToken.None);
                        foreach (var (line, result) in lines.Zip(results))
                            BreakpointChanged?.Invoke(file, line.Line, result.Verified);
                    }
                    if (capabilities.ExceptionFilters.Count > 0)
                        await SetExceptionBreakpointsAsync(child, _exceptionFilters, CancellationToken.None);
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

    List<KeyValuePair<string, IReadOnlyList<SourceBreakpoint>>> Breakpoints()
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
        // What runs in its terminal says the rest of the program's output as it ends.
        for (var i = 0; i < 600 && TerminalCount() > 0; i++)
            await Task.Delay(50);
        End(_exitCode);
    });

    int TerminalCount()
    {
        lock (_terminals)
            return _terminals.Count;
    }

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

    /// <summary>The exceptions it breaks on now (filters of its <see cref="Capabilities"/>), in each process debugged.</summary>
    public async Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filters, CancellationToken ct = default)
    {
        _exceptionFilters = filters;
        if (Capabilities.ExceptionFilters.Count == 0)
            return;
        foreach (var dap in Debuggees())
            await SetExceptionBreakpointsAsync(dap, filters, ct);
    }

    static Task SetExceptionBreakpointsAsync(DapConnection dap, IReadOnlyList<string> filters, CancellationToken ct) =>
        dap.RequestAsync("setExceptionBreakpoints", new { filters }, ct);

    /// <summary>A file's breakpoints now (all of them: the lines replace those it had), in each process debugged.</summary>
    public async Task<IReadOnlyList<BreakpointResult>> SetBreakpointsAsync(string file, IReadOnlyList<SourceBreakpoint> lines, CancellationToken ct = default)
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

    async Task<IReadOnlyList<BreakpointResult>> SetBreakpointsAsync(DapConnection dap, string file, IReadOnlyList<SourceBreakpoint> lines, CancellationToken ct)
    {
        var body = await dap.RequestAsync("setBreakpoints", new
        {
            source = new { path = file, name = Path.GetFileName(file) },
            // A condition, hit count or message only when there is one (and only to a debugger that can: it ignores what it cannot).
            breakpoints = lines.Select(b =>
            {
                var breakpoint = new JsonObject { ["line"] = b.Line };
                if (b.Condition is { } condition)
                    breakpoint["condition"] = condition;
                if (b.HitCondition is { } hit)
                    breakpoint["hitCondition"] = hit;
                if (b.LogMessage is { } message)
                    breakpoint["logMessage"] = message;
                return breakpoint;
            }).ToArray(),
            lines = lines.Select(b => b.Line).ToArray(),
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
                    _breakpointIds[(dap, id.GetInt32())] = (file, lines[i].Line);
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
            // A file's URI where a path was asked for (kotlin-lsp's attached sessions say file:///...): its path.
            if (path is not null && path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
                path = FileUris.ToPath(uri);
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
        List<ProcessRunner> terminals;
        lock (_terminals)
            terminals = [.. _terminals];
        foreach (var terminal in terminals)
            terminal.Kill();
        List<DapConnection> children;
        lock (_children)
            children = [.. _children];
        foreach (var child in children)
            await child.DisposeAsync();
        await _root.DisposeAsync();
        await _adapter.DisposeAsync();
    }
}
