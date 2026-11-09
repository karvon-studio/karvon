using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// An agent's program that speaks JSON-RPC on its standard input and output, a message per line (Codex's app-server, an Agent
/// Client Protocol agent): Karvon's requests and their answers, and the agent's own requests and notifications.
/// </summary>
public sealed class JsonRpcProcess : IAsyncDisposable
{
    readonly Process _process;
    readonly StreamWriter _stdin;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly StringBuilder _stderr = new();
    readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly bool _version;
    long _nextId;

    JsonRpcProcess(Process process, bool version)
    {
        _process = process;
        _version = version;
        _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>The agent asks something (method, params, its id): the answer is sent with <see cref="RespondAsync"/>.</summary>
    public event Action<string, JsonElement, JsonNode?>? Request;

    /// <summary>The agent tells something (method, params).</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>The program ended (its exit code).</summary>
    public event Action<int>? Exited;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>
    /// Starts <paramref name="executable"/> in <paramref name="workingDirectory"/>; <paramref name="environment"/> may change its
    /// environment first. With <paramref name="version"/> each message says "jsonrpc": "2.0".
    /// </summary>
    public static JsonRpcProcess Start(string executable, IEnumerable<string> arguments, string workingDirectory,
        Action<IDictionary<string, string?>>? environment = null, bool version = true)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["PWD"] = workingDirectory;
        environment?.Invoke(psi.Environment);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"{executable} could not be started.");
        var rpc = new JsonRpcProcess(process, version);
        _ = rpc.ReadAsync();
        _ = rpc.ReadErrorsAsync();
        return rpc;
    }

    /// <summary>The last lines the program wrote to its standard error (why it quit).</summary>
    public string StderrTail(int lines)
    {
        lock (_stderr)
            return string.Join("\n", _stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines)).Trim();
    }

    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        var message = Message();
        message["id"] = id;
        message["method"] = method;
        if (parameters is not null)
            message["params"] = parameters;
        try
        {
            await WriteAsync(message);
            using var registration = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
            return await answer.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters = null)
    {
        var message = Message();
        message["method"] = method;
        if (parameters is not null)
            message["params"] = parameters;
        return WriteAsync(message);
    }

    /// <summary>Answers a request of the agent's (an answer to a program that has ended goes nowhere).</summary>
    public Task RespondAsync(JsonNode? id, JsonNode? result)
    {
        var message = Message();
        message["id"] = id?.DeepClone();
        message["result"] = result?.DeepClone();
        return AnswerAsync(message);
    }

    public Task RespondErrorAsync(JsonNode? id, int code, string text)
    {
        var message = Message();
        message["id"] = id?.DeepClone();
        message["error"] = new JsonObject { ["code"] = code, ["message"] = text };
        return AnswerAsync(message);
    }

    async Task AnswerAsync(JsonObject message)
    {
        try
        {
            await WriteAsync(message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    JsonObject Message() => _version ? new JsonObject { ["jsonrpc"] = "2.0" } : [];

    async Task WriteAsync(JsonObject message)
    {
        if (HasExited)
            throw new IOException("The agent's program has ended.");
        await _writeLock.WaitAsync();
        try
        {
            await _stdin.WriteLineAsync(message.ToJsonString());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0)
                    continue;
                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message.ValueKind != JsonValueKind.Object)
                    continue;
                Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        try
        {
            await _process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException(StderrTail(6) is { Length: > 0 } tail ? tail : "The agent's program has ended."));
        _exited.TrySetResult();
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            code = -1;
        }
        Exited?.Invoke(code);
    }

    void Dispatch(JsonElement message)
    {
        var hasMethod = message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (hasMethod && hasId)
            Request?.Invoke(method.GetString()!, parameters, JsonNode.Parse(id.GetRawText()));
        else if (hasMethod)
            Notification?.Invoke(method.GetString()!, parameters);
        else if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var answer))
        {
            if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                answer.TrySetException(new InvalidOperationException(ClaudeJson.Str(error, "message") ?? "The agent refused the request."));
            else
                answer.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
        }
    }

    async Task ReadErrorsAsync()
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer)) > 0)
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    // Only the end is shown.
                    if (_stderr.Length > 64_000)
                        _stderr.Remove(0, _stderr.Length - 32_000);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        if (!HasExited)
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                // Its reader sees it end before the process is let go.
                try
                {
                    await _exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }
            }
        _process.Dispose();
    }
}
