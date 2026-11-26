using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Claude.Remote;

/// <summary>
/// Several Karvon windows, one bot: the window that holds <see cref="LockFile"/> polls Telegram and hands each of the others,
/// over a Unix socket beside that file, what is theirs: a button pressed on their question, a message in their conversation's
/// topic, one for a past conversation of their folder. Before anything else is said each side proves it knows a key kept in
/// the vault's sealed part (each for its own side, so neither proof can be passed off as the other's): a program of the
/// user's that is not Karvon, an agent's among them, can pass neither as a window nor as the one that polls. When that one
/// closes, another window takes the bot over.
/// </summary>
public sealed partial class TelegramRemote
{
    const string LinkKey = "karvon-telegram/link";

    /// <summary>Another window, as the one that polls sees it: what it says is its own.</summary>
    sealed class Peer(Stream stream)
    {
        readonly SemaphoreSlim _writing = new(1);

        public HashSet<int> Threads { get; set; } = [];

        public HashSet<string> Keys { get; set; } = [];

        public HashSet<string> Folders { get; set; } = new(StringComparer.Ordinal);

        public async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            await _writing.WaitAsync(ct);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                _writing.Release();
            }
        }
    }

    readonly List<Peer> _peers = [];
    readonly Dictionary<string, Func<string>> _folders = [];
    Peer? _link;
    string? _announced;

    /// <summary>Another window polls the bot, and this one gets what is its own from it.</summary>
    public bool IsLinked => _link is not null;

    /// <summary>How long a window waits before it tries again to reach the one that polls (or to become it).</summary>
    public TimeSpan LinkRetry { get; set; } = TimeSpan.FromSeconds(2);

    string SocketPath => Path.ChangeExtension(LockFile, ".sock");

    /// <summary>Polls the bot when no other window does; else is handed its own by the one that does, until that one closes.</summary>
    async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            if (TakeLock())
            {
                Changed?.Invoke();
                await OwnAsync(cancel);
                return;
            }
            await LinkAsync(cancel);
            try
            {
                await Task.Delay(LinkRetry, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- The window that polls ------------------------------------------------------------------------------------

    async Task OwnAsync(CancellationToken cancel)
    {
        var secret = _store.Get(LinkKey) is { } kept ? Convert.FromBase64String(kept) : null;
        if (secret is null)
        {
            secret = RandomNumberGenerator.GetBytes(32);
            _store.Set(LinkKey, "Telegram windows", Convert.ToBase64String(secret));
        }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Task serving = Task.CompletedTask;
        try
        {
            File.Delete(SocketPath);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(8);
            serving = Task.Run(() => ServeAsync(listener, secret, cancel), CancellationToken.None);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            // The other windows cannot reach it: this one goes on with its own conversations.
        }
        try
        {
            await PollAsync(cancel);
        }
        finally
        {
            listener.Close();
            try
            {
                await serving;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            try
            {
                File.Delete(SocketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    async Task ServeAsync(Socket listener, byte[] secret, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(cancel);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            _ = Task.Run(() => PeerAsync(socket, secret, cancel), CancellationToken.None);
        }
    }

    async Task PeerAsync(Socket socket, byte[] secret, CancellationToken cancel)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var peer = new Peer(stream);
        try
        {
            using var hurry = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            hurry.CancelAfter(TimeSpan.FromSeconds(5));
            var nonce = RandomNumberGenerator.GetBytes(32);
            await peer.SendAsync(new JsonObject { ["t"] = "hello", ["nonce"] = Convert.ToBase64String(nonce) }, hurry.Token);
            if (await ReadAsync(reader, hurry.Token) is not { } auth || Str(auth, "t") != "auth" || !Proves(secret, "window", nonce, Str(auth, "mac"))
                || Str(auth, "nonce") is not { } theirs)
                return;
            await peer.SendAsync(new JsonObject { ["t"] = "ok", ["mac"] = Mac(secret, "poller", Convert.FromBase64String(theirs)) }, hurry.Token);
            lock (_lock)
                _peers.Add(peer);
            while (await ReadAsync(reader, cancel) is { } message)
                if (Str(message, "t") == "own")
                    lock (_lock)
                    {
                        peer.Threads = [.. Items(message, "threads").Select(n => n.GetValue<int>())];
                        peer.Keys = [.. Items(message, "keys").Select(n => n.GetValue<string>())];
                        peer.Folders = new(Items(message, "folders").Select(n => n.GetValue<string>()), StringComparer.Ordinal);
                    }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or FormatException or InvalidOperationException)
        {
        }
        finally
        {
            lock (_lock)
                _peers.Remove(peer);
        }
    }

    /// <summary>An update that is another window's, handed to it; false when it is this one's (or no one's).</summary>
    async Task<bool> ForwardAsync(TelegramUpdate update)
    {
        if (Pairing is not { } pairing)
            return false;
        Peer? to = null;
        if (update.Callback is { Data: { Length: > 2 } data } callback && callback.From.Id == pairing.UserId
            && data.Split(':') is [_, var key, ..] && !_asks.ContainsKey(key))
        {
            lock (_lock)
                to = _peers.FirstOrDefault(p => p.Keys.Contains(key));
        }
        else if (update.Message is { IsTopicMessage: true, ThreadId: { } thread } message && message.From?.Id == pairing.UserId && message.Chat.Id == pairing.ChatId)
        {
            lock (_lock)
                if (!_chats.Any(c => c.ThreadId == thread))
                    to = _peers.FirstOrDefault(p => p.Threads.Contains(thread));
            // A past conversation's: the window of its folder opens it again.
            if (to is null && Topics().Values.FirstOrDefault(t => t.Thread == thread) is { Folder: { } folder } && !OwnsFolder(folder))
                lock (_lock)
                    to = _peers.FirstOrDefault(p => p.Folders.Contains(Folder(folder)));
        }
        if (to is null)
            return false;
        try
        {
            await to.SendAsync(new JsonObject { ["t"] = "update", ["u"] = JsonSerializer.SerializeToNode(update) }, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    bool OwnsFolder(string folder) => _folders.Values.Any(f => Folder(f()) == Folder(folder));

    static string Folder(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // ---- A window that does not poll -----------------------------------------------------------------------------------

    async Task LinkAsync(CancellationToken cancel)
    {
        if (!File.Exists(SocketPath) || _store.Get(LinkKey) is not { } kept)
            return;
        var secret = Convert.FromBase64String(kept);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Peer? link = null;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancel);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var peer = new Peer(stream);
            using (var hurry = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                hurry.CancelAfter(TimeSpan.FromSeconds(5));
                if (await ReadAsync(reader, hurry.Token) is not { } hello || Str(hello, "t") != "hello" || Str(hello, "nonce") is not { } nonce)
                    return;
                var mine = RandomNumberGenerator.GetBytes(32);
                await peer.SendAsync(new JsonObject
                {
                    ["t"] = "auth",
                    ["mac"] = Mac(secret, "window", Convert.FromBase64String(nonce)),
                    ["nonce"] = Convert.ToBase64String(mine),
                }, hurry.Token);
                if (await ReadAsync(reader, hurry.Token) is not { } ok || Str(ok, "t") != "ok" || !Proves(secret, "poller", mine, Str(ok, "mac")))
                    return;
            }
            link = _link = peer;
            _announced = null;
            Changed?.Invoke();
            Announce();
            while (await ReadAsync(reader, cancel) is { } message)
                if (Str(message, "t") == "update" && message["u"]?.Deserialize<TelegramUpdate>() is { } update && Token is { } token)
                    await OnUIAsync(() => HandleAsync(Bot(token), update, forwarded: true));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or FormatException or InvalidOperationException)
        {
        }
        finally
        {
            if (link is not null)
            {
                _link = null;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>What is this window's told to the one that polls, when it changed: its topics, its questions, its folders.</summary>
    void Announce()
    {
        if (_link is not { } link)
            return;
        JsonObject own;
        lock (_lock)
            own = new JsonObject
            {
                ["t"] = "own",
                ["threads"] = new JsonArray([.. _chats.Select(c => c.ThreadId).OfType<int>().Distinct().Select(t => (JsonNode)t)]),
                ["keys"] = new JsonArray([.. _asks.Keys.Select(k => (JsonNode)k)]),
                ["folders"] = new JsonArray([.. _folders.Values.Select(f => (JsonNode)Folder(f())).DistinctBy(n => n.GetValue<string>())]),
            };
        var text = own.ToJsonString();
        if (text == _announced)
            return;
        _announced = text;
        _ = Task.Run(async () =>
        {
            try
            {
                await link.SendAsync(own, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
            }
        });
    }

    // ---- Both sides ---------------------------------------------------------------------------------------------------

    static async Task<JsonObject?> ReadAsync(StreamReader reader, CancellationToken ct) =>
        await reader.ReadLineAsync(ct) is { } line ? JsonNode.Parse(line) as JsonObject : null;

    static string? Str(JsonObject message, string name) => message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static IEnumerable<JsonNode> Items(JsonObject message, string name) => message[name] is JsonArray array ? array.OfType<JsonNode>() : [];

    /// <summary>The proof of a side (<paramref name="side"/>: "window" or "poller") that it knows the key, for a nonce.</summary>
    static string Mac(byte[] secret, string side, byte[] nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(side + ":").Concat(nonce).ToArray()));

    static bool Proves(byte[] secret, string side, byte[] nonce, string? mac)
    {
        if (mac is null)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(mac), Convert.FromBase64String(Mac(secret, side, nonce)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
