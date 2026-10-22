using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bengine.Introspection.Servers;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Karvon.Servers;

/// <summary>A connection or a command that failed, in words the user can act on.</summary>
public sealed class ServerException(string message) : Exception(message);

public sealed record ServerResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Standard output and error together, the error last.</summary>
    public string Combined => string.Join("\n", new[] { Output, Error }.Where(s => s.Length > 0));
}

/// <summary>What the connections ask the user: a password or passphrase, and whether to trust a new host key.</summary>
public interface IServerPrompts
{
    /// <summary>A secret, and whether to keep it in the system keyring; null when the user cancels.</summary>
    Task<(string Secret, bool Save)?> SecretAsync(ServerHost host, string question);

    /// <summary>Whether to trust a host key Karvon has not seen (and ~/.ssh/known_hosts does not have).</summary>
    Task<bool> TrustAsync(ServerHost host, string endpoint, string keyType, string fingerprint);
}

/// <summary>
/// SSH connections to the Servers explorer's machines (SSH.NET), one per machine, kept alive. Keys come from the host's
/// IdentityFile (or ~/.ssh/id_*), with their passphrase asked; a password is asked when no key gets in. Secrets are kept
/// for the session, and in the system keyring when the user says so. A host key is trusted when Karvon or
/// ~/.ssh/known_hosts knows it; a new one is shown to the user first; a changed one is refused.
/// </summary>
public sealed class SshConnections(string knownHostsFile, IServerPrompts prompts, Func<string, ServerHost?> findHost, ICredentialStore? keyring = null) : IDisposable
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    sealed class Connection(SshClient client, ForwardedPortLocal? tunnel)
    {
        public SshClient Client { get; } = client;
        public ForwardedPortLocal? Tunnel { get; } = tunnel;
        public SftpClient? Sftp { get; set; }
        public TimeSpan Latency { get; set; }
    }

    readonly Dictionary<string, Connection> _connections = [];
    readonly Dictionary<string, SemaphoreSlim> _locks = [];
    readonly Dictionary<string, string> _secrets = [];

    /// <summary>Raised (on any thread) when a machine connects, fails or is let go.</summary>
    public event Action<string>? Changed;

    public bool IsConnected(string name)
    {
        lock (_connections)
            return _connections.TryGetValue(name, out var c) && c.Client.IsConnected;
    }

    /// <summary>The round trip of a command when it connected (or was last measured).</summary>
    public TimeSpan? Latency(string name)
    {
        lock (_connections)
            return _connections.TryGetValue(name, out var c) && c.Client.IsConnected ? c.Latency : null;
    }

    SemaphoreSlim Lock(string name)
    {
        lock (_locks)
            return _locks.TryGetValue(name, out var l) ? l : _locks[name] = new SemaphoreSlim(1, 1);
    }

    /// <summary>The machine's connection, made (asking for what it needs) when there is none.</summary>
    public async Task<SshClient> ClientAsync(ServerHost host, CancellationToken cancellationToken = default) => (await ConnectionAsync(host, cancellationToken)).Client;

    async Task<Connection> ConnectionAsync(ServerHost host, CancellationToken cancellationToken)
    {
        var gate = Lock(host.Name);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (_connections)
                if (_connections.TryGetValue(host.Name, out var open) && open.Client.IsConnected)
                    return open;
            Drop(host.Name);
            var connection = await ConnectAsync(host, cancellationToken);
            lock (_connections)
                _connections[host.Name] = connection;
            Changed?.Invoke(host.Name);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ServerException)
        {
            throw new ServerException($"Could not connect to {host.Name} ({host.Target}): {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<Connection> ConnectAsync(ServerHost host, CancellationToken cancellationToken)
    {
        // Through a jump host: a local port forwarded to the machine on the jump host's connection.
        ForwardedPortLocal? tunnel = null;
        var (address, port) = (host.Address, host.Port);
        if (host.ProxyJump is { } jump)
        {
            var via = (findHost(jump) ?? Parse(jump, host.User)) with { ProxyJump = null };
            var jumpClient = await ClientAsync(via, cancellationToken);
            tunnel = new ForwardedPortLocal("127.0.0.1", 0, host.Address, (uint)host.Port);
            jumpClient.AddForwardedPort(tunnel);
            tunnel.Start();
            (address, port) = ("127.0.0.1", (int)tunnel.BoundPort);
        }
        var keyProblems = new List<string>();
        var keys = await LoadKeysAsync(host, keyProblems.Add);
        string? password = Secret("password:" + host.Name);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var methods = new List<AuthenticationMethod>();
            if (keys.Count > 0)
                methods.Add(new PrivateKeyAuthenticationMethod(host.User, [.. keys]));
            if (password is not null)
            {
                methods.Add(new PasswordAuthenticationMethod(host.User, password));
                var interactive = new KeyboardInteractiveAuthenticationMethod(host.User);
                var answer = password;
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                        prompt.Response = answer;
                };
                methods.Add(interactive);
            }
            // No key and no password yet: connect anyway, so a machine that does not answer says so at once and its host key is
            // checked before a password is asked.
            if (methods.Count == 0)
                methods.Add(new NoneAuthenticationMethod(host.User));
            var info = new ConnectionInfo(address, port, host.User, [.. methods]) { Timeout = ConnectTimeout };
            var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
            var check = new HostKeyCheck(this, host);
            client.HostKeyReceived += check.OnHostKey;
            try
            {
                var clock = Stopwatch.StartNew();
                await client.ConnectAsync(cancellationToken);
                clock.Stop();
                var connection = new Connection(client, tunnel) { Latency = clock.Elapsed };
                await MeasureAsync(connection, cancellationToken);
                return connection;
            }
            catch (SshConnectionException) when (check.Unknown is { } unknown)
            {
                client.Dispose();
                // A key Karvon has not seen: shown to the user, kept when trusted, and the connection tried again.
                if (!await prompts.TrustAsync(host, check.Endpoint, unknown.Type, unknown.Fingerprint))
                    throw new ServerException($"{host.Name}'s host key was not trusted, so Karvon did not connect.");
                Trust(check.Endpoint, unknown.Fingerprint);
            }
            catch (SshConnectionException) when (check.Changed is { } changed)
            {
                client.Dispose();
                throw new ServerException(
                    $"The host key of {check.Endpoint} changed (was {changed.Was}, now {changed.Now}); Karvon does not connect. If the machine was reinstalled, remove its old key (Forget Host Key) and connect again.");
            }
            catch (SshAuthenticationException ex)
            {
                client.Dispose();
                Forget("password:" + host.Name);
                password = await AskPasswordAsync(host, password is null ? string.Join(" ", keyProblems) : "The password was not accepted. " + ex.Message);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new ServerException($"{host.Name} did not accept the keys or the password.");
    }

    /// <summary>The round trip of a command that does nothing (the status bar's latency).</summary>
    static async Task MeasureAsync(Connection connection, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var command = connection.Client.CreateCommand("true");
        command.CommandTimeout = ConnectTimeout;
        await command.ExecuteAsync(cancellationToken);
        connection.Latency = clock.Elapsed;
    }

    /// <summary>Measures the latency again; null when the machine is not connected.</summary>
    public async Task<TimeSpan?> PingAsync(ServerHost host, CancellationToken cancellationToken = default)
    {
        Connection? connection;
        lock (_connections)
            _connections.TryGetValue(host.Name, out connection);
        if (connection is not { Client.IsConnected: true })
            return null;
        await MeasureAsync(connection, cancellationToken);
        Changed?.Invoke(host.Name);
        return connection.Latency;
    }

    async Task<List<IPrivateKeySource>> LoadKeysAsync(ServerHost host, Action<string> problem)
    {
        var keys = new List<IPrivateKeySource>();
        foreach (var file in (host.IdentityFiles.Count > 0 ? host.IdentityFiles : SshConfig.DefaultKeys).Where(File.Exists))
        {
            var passphrase = Secret("passphrase:" + file);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    keys.Add(passphrase is null ? new PrivateKeyFile(file) : new PrivateKeyFile(file, passphrase));
                    break;
                }
                catch (SshPassPhraseNullOrEmptyException)
                {
                    passphrase = await AskAsync(host, "passphrase:" + file, $"Passphrase for {file}");
                }
                catch (SshException ex) when (passphrase is not null)
                {
                    Forget("passphrase:" + file);
                    passphrase = await AskAsync(host, "passphrase:" + file, $"The passphrase was wrong ({ex.Message}). Passphrase for {file}");
                }
                catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    problem($"{file} could not be read: {ex.Message}.");
                    break;
                }
                if (passphrase is null)
                    break;
            }
        }
        return keys;
    }

    async Task<string?> AskPasswordAsync(ServerHost host, string why)
    {
        var password = await AskAsync(host, "password:" + host.Name, (why.Length > 0 ? why + " " : "") + $"Password for {host.Target}");
        return password ?? throw new ServerException($"No key or password for {host.Name}: add an IdentityFile in ~/.ssh/config, or connect with a password.");
    }

    async Task<string?> AskAsync(ServerHost host, string key, string question)
    {
        if (await prompts.SecretAsync(host, question) is not { } answer)
            return null;
        lock (_secrets)
            _secrets[key] = answer.Secret;
        if (answer.Save)
            keyring?.Set("karvon-ssh/" + key, $"Karvon: {question}", answer.Secret);
        return answer.Secret;
    }

    string? Secret(string key)
    {
        lock (_secrets)
            if (_secrets.TryGetValue(key, out var secret))
                return secret;
        return keyring?.Get("karvon-ssh/" + key);
    }

    void Forget(string key)
    {
        lock (_secrets)
            _secrets.Remove(key);
        keyring?.Delete("karvon-ssh/" + key);
    }

    /// <summary>Forgets the passwords kept for a machine (this session's and the keyring's).</summary>
    public void ForgetSecrets(ServerHost host)
    {
        Forget("password:" + host.Name);
        foreach (var file in host.IdentityFiles.Count > 0 ? host.IdentityFiles : SshConfig.DefaultKeys)
            Forget("passphrase:" + file);
    }

    static ServerHost Parse(string target, string user)
    {
        var at = target.IndexOf('@');
        var hostPort = at >= 0 ? target[(at + 1)..] : target;
        var colon = hostPort.LastIndexOf(':');
        var port = colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var p) ? p : 22;
        var address = colon > 0 ? hostPort[..colon] : hostPort;
        return new ServerHost(address, address, port, at >= 0 ? target[..at] : user);
    }

    // ---- Host keys ------------------------------------------------------------------------------------------------

    /// <summary>The host key check of one connection attempt: what it saw, when it did not trust it.</summary>
    sealed class HostKeyCheck(SshConnections owner, ServerHost host)
    {
        public string Endpoint { get; } = $"{host.Address}:{host.Port}";

        public (string Type, string Fingerprint)? Unknown { get; private set; }

        public (string Was, string Now)? Changed { get; private set; }

        public void OnHostKey(object? sender, HostKeyEventArgs e)
        {
            var fingerprint = "SHA256:" + e.FingerPrintSHA256;
            var kept = owner.Known(Endpoint);
            var known = KnownHosts.Find(Path.Combine(SshConfig.Home, ".ssh", "known_hosts"), host.Address, host.Port, e.HostKeyName);
            var key = Convert.ToBase64String(e.HostKey);
            if (kept == fingerprint || known.Contains(key))
            {
                e.CanTrust = true;
                return;
            }
            e.CanTrust = false;
            if (kept is not null)
                Changed = (kept, fingerprint);
            else if (known.Count > 0)
                Changed = ("the key in ~/.ssh/known_hosts", fingerprint);
            else
                Unknown = (e.HostKeyName, fingerprint);
        }
    }

    Dictionary<string, string> KnownKeys()
    {
        try
        {
            return File.Exists(knownHostsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(knownHostsFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    string? Known(string endpoint)
    {
        lock (_locks)
            return KnownKeys().GetValueOrDefault(endpoint);
    }

    void Trust(string endpoint, string fingerprint)
    {
        lock (_locks)
        {
            var keys = KnownKeys();
            keys[endpoint] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(knownHostsFile)!);
            File.WriteAllText(knownHostsFile, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Forgets the host key Karvon kept for a machine (it was reinstalled): the next connection shows the new one.</summary>
    public void ForgetHostKey(ServerHost host)
    {
        lock (_locks)
        {
            var keys = KnownKeys();
            if (keys.Remove($"{host.Address}:{host.Port}"))
                File.WriteAllText(knownHostsFile, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // ---- Commands and files ---------------------------------------------------------------------------------------

    /// <summary>Runs a command through the machine's login shell, to its end.</summary>
    public async Task<ServerResult> RunAsync(ServerHost host, string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync(host, cancellationToken);
        using var ssh = client.CreateCommand(command);
        ssh.CommandTimeout = timeout ?? TimeSpan.FromMinutes(2);
        try
        {
            await ssh.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            return new ServerResult(-1, ssh.Result.TrimEnd(), $"The command did not finish in {ssh.CommandTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is SshConnectionException or SocketException or ObjectDisposedException)
        {
            Drop(host.Name);
            throw new ServerException($"The connection to {host.Name} was lost: {ex.Message}");
        }
        return new ServerResult(ssh.ExitStatus ?? -1, ssh.Result.TrimEnd(), ssh.Error.TrimEnd());
    }

    /// <summary>
    /// Runs a command that goes on (journalctl -f, tail -F), each line of its output to <paramref name="onLine"/>, until it
    /// ends or <paramref name="cancellationToken"/> stops it.
    /// </summary>
    public async Task StreamAsync(ServerHost host, string command, Action<string> onLine, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(host, cancellationToken);
        using var ssh = client.CreateCommand(command);
        var running = ssh.BeginExecute();
        using var stop = cancellationToken.Register(() => ssh.CancelAsync(forceKill: true));
        try
        {
            using var reader = new StreamReader(ssh.OutputStream, Encoding.UTF8);
            while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken) is { } line)
                onLine(line);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException)
        {
            if (!cancellationToken.IsCancellationRequested)
                onLine($"[{ex.Message}]");
        }
        finally
        {
            try
            {
                if (!running.IsCompleted)
                    ssh.CancelAsync(forceKill: true);
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
                // It ended meanwhile.
            }
        }
    }

    /// <summary>The machine's SFTP session, opened on its connection's credentials when there is none.</summary>
    public async Task<SftpClient> SftpAsync(ServerHost host, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectionAsync(host, cancellationToken);
        if (connection.Sftp is { IsConnected: true } open)
            return open;
        connection.Sftp?.Dispose();
        var sftp = new SftpClient(connection.Client.ConnectionInfo) { KeepAliveInterval = KeepAlive };
        var check = new HostKeyCheck(this, host);
        sftp.HostKeyReceived += check.OnHostKey;
        try
        {
            await sftp.ConnectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SshException or SocketException)
        {
            sftp.Dispose();
            throw new ServerException($"Could not open the files of {host.Name}: {ex.Message}");
        }
        return connection.Sftp = sftp;
    }

    /// <summary>Lets go of a machine's connection (Connect Again, or Karvon closing).</summary>
    public void Drop(string name)
    {
        Connection? old;
        lock (_connections)
            if (_connections.Remove(name, out old) is false)
                return;
        try
        {
            old!.Sftp?.Dispose();
            old.Tunnel?.Stop();
            old.Client.Dispose();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException)
        {
        }
        Changed?.Invoke(name);
    }

    public void Dispose()
    {
        List<string> names;
        lock (_connections)
            names = [.. _connections.Keys];
        foreach (var name in names)
            Drop(name);
    }
}

/// <summary>~/.ssh/known_hosts: the keys OpenSSH has for a host, hashed names included.</summary>
public static class KnownHosts
{
    /// <summary>The base64 keys of this type known for the host (and port), from a known_hosts file.</summary>
    public static IReadOnlyList<string> Find(string file, string host, int port, string keyType)
    {
        if (!File.Exists(file))
            return [];
        var name = port == 22 ? host : $"[{host}]:{port}";
        var keys = new List<string>();
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('@'))
                    continue;
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || parts[1] != keyType)
                    continue;
                if (parts[0].StartsWith("|1|", StringComparison.Ordinal) ? HashedMatches(parts[0], name) : parts[0].Split(',').Any(n => n == name || SshConfig.Matches([n], name)))
                    keys.Add(parts[2]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return keys;
    }

    /// <summary>"|1|salt|hash": HMAC-SHA1 of the name with the salt as key.</summary>
    static bool HashedMatches(string entry, string name)
    {
        var parts = entry.Split('|');
        if (parts.Length != 4)
            return false;
        try
        {
            using var hmac = new HMACSHA1(Convert.FromBase64String(parts[2]));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(name))) == parts[3];
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
