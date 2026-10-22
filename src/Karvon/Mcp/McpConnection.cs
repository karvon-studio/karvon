using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Karvon.Claude.Tools;

namespace Karvon.Mcp;

public enum McpStatus
{
    NotConnected,
    Connecting,
    Connected,
    NeedsSignIn,
    Failed,
}

/// <summary>What a tool call, a read or a prompt gave: its text, and whether it is an error.</summary>
public sealed record McpReply(string Text, bool IsError = false);

/// <summary>
/// Karvon's own connection to one MCP server, made when the server is opened (never at start: npx may download it first):
/// what it offers (tools, resources, prompts), calls to them, and a log of everything said, the server's stderr with it.
/// </summary>
public sealed class McpConnection(McpServerDefinition server, string? workingDirectory, McpSignIn? signIn = null) : IAsyncDisposable
{
    const int MaxLog = 2000;
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Time to sign in in the browser.</summary>
    static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    static readonly JsonSerializerOptions Pretty = new(McpJsonUtilities.DefaultOptions) { WriteIndented = true };

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly List<string> _log = [];
    McpClient? _client;
    bool _disposed;

    /// <summary>Its definition; a refresh of the configurations brings the new one (used on the next connection).</summary>
    public McpServerDefinition Server { get; set; } = server;

    public McpStatus Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>"name version", as the server says at the handshake.</summary>
    public string? ServerInfo { get; private set; }

    public IReadOnlyList<Tool> Tools { get; private set; } = [];

    public IReadOnlyList<Resource> Resources { get; private set; } = [];

    public IReadOnlyList<Prompt> Prompts { get; private set; } = [];

    /// <summary>The status changed (on any thread).</summary>
    public event Action? Changed;

    /// <summary>A line was logged (on any thread).</summary>
    public event Action<string>? Logged;

    public bool IsConnected => Status == McpStatus.Connected;

    /// <summary>The browser is open at the server's sign-in page.</summary>
    public bool IsSigningIn { get; private set; }

    /// <summary>A URL that can be signed in to (stdio servers have their environment instead).</summary>
    public bool CanSignIn => Server.IsHttp && signIn is not null;

    /// <summary>Karvon has its tokens (it signed in before): the next connection uses them.</summary>
    public bool IsSignedIn => CanSignIn && Tokens!.HasTokens;

    McpTokens? Tokens => Server.Url is { } url && signIn is not null ? signIn.TokensFor(McpConfigs.Expand(url)) : null;

    public string StatusText => Status switch
    {
        McpStatus.Connected => "connected · " + Counts,
        McpStatus.Connecting when IsSigningIn => "signing in: finish in your browser…",
        McpStatus.Connecting => "connecting…",
        McpStatus.NeedsSignIn => "needs sign-in",
        McpStatus.Failed => "failed: " + Error,
        _ => "not connected",
    };

    string Counts => string.Join(", ", new[]
    {
        Count(Tools.Count, "tool"),
        Resources.Count > 0 ? Count(Resources.Count, "resource") : null,
        Prompts.Count > 0 ? Count(Prompts.Count, "prompt") : null,
    }.OfType<string>());

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    public string LogText
    {
        get
        {
            lock (_log)
                return string.Join("\n", _log);
        }
    }

    /// <summary>
    /// Connects again (ending the old connection): the handshake, then what the server offers. A server that wants a
    /// sign-in gets the tokens Karvon has for it; without them it waits for <see cref="SignInAsync"/> (no browser opens by itself).
    /// </summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) => ConnectAsync(interactive: false, cancellationToken);

    /// <summary>Signs in in the browser (OAuth: the server's sign-in page, then back to Karvon), then connects with the tokens.</summary>
    public Task SignInAsync(CancellationToken cancellationToken = default) => ConnectAsync(interactive: true, cancellationToken);

    /// <summary>Forgets the tokens (here and in the keyring) and disconnects.</summary>
    public async Task SignOutAsync()
    {
        Tokens?.Forget();
        await DisconnectAsync();
        Write("info", "signed out");
    }

    async Task ConnectAsync(bool interactive, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseAsync();
            var server = McpConfigs.Expand(Server);
            IsSigningIn = interactive && CanSignIn;
            SetStatus(McpStatus.Connecting, null);
            Write("info", (IsSigningIn ? "signing in, then connecting over " : "connecting over ") + server.Transport);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IsSigningIn ? SignInTimeout : Timeout);
            try
            {
                IClientTransport transport = server.IsHttp
                    ? new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Name = server.Name,
                        Endpoint = new Uri(server.Url!),
                        AdditionalHeaders = server.Headers.ToDictionary(),
                        TransportMode = server.IsSse ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
                        OAuth = await OAuthForAsync(server, IsSigningIn, timeout.Token),
                    })
                    : new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = server.Name,
                        Command = server.Command ?? "",
                        Arguments = server.Args.ToList(),
                        EnvironmentVariables = server.Env.ToDictionary(p => p.Key, p => (string?)p.Value),
                        WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                        StandardErrorLines = line => Write("stderr", line),
                    });
                var client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "karvon", Version = typeof(McpConnection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" },
                    InitializationTimeout = Timeout,
                }, cancellationToken: timeout.Token);
                _client = client;
                if (_disposed)
                {
                    // Karvon closed while the server started.
                    await CloseAsync();
                    return;
                }
                ServerInfo = $"{client.ServerInfo.Name} {client.ServerInfo.Version}".Trim();
                var capabilities = client.ServerCapabilities;
                Tools = capabilities.Tools is null ? [] : [.. (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(t => t.ProtocolTool)];
                Resources = capabilities.Resources is null ? [] : [.. (await client.ListResourcesAsync(cancellationToken: timeout.Token)).Select(r => r.ProtocolResource)];
                Prompts = capabilities.Prompts is null ? [] : [.. (await client.ListPromptsAsync(cancellationToken: timeout.Token)).Select(p => p.ProtocolPrompt)];
                IsSigningIn = false;
                SetStatus(McpStatus.Connected, null);
                Write("info", StatusText);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                IsSigningIn = false;
                await CloseAsync();
                SetStatus(McpStatus.NotConnected, null);
            }
            // Whatever a server (or the program that is not one) does wrong ends here: its status says so.
            catch (Exception ex)
            {
                var signingIn = IsSigningIn;
                IsSigningIn = false;
                var message = ex is OperationCanceledException ? signingIn ? "the sign-in was not finished within five minutes" : "it did not answer within two minutes" : Explain(Describe(ex));
                Write("error", message);
                await CloseAsync();
                var wantsSignIn = ex is SignInNeededException || ex.GetBaseException() is SignInNeededException || message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal)
                    || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
                SetStatus(wantsSignIn && !signingIn ? McpStatus.NeedsSignIn : McpStatus.Failed, wantsSignIn && !signingIn ? "the server wants you to sign in" : message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A sign-in is needed, and the connection was not asked to open the browser.</summary>
    sealed class SignInNeededException() : Exception("the server wants you to sign in");

    /// <summary>
    /// OAuth for a URL: the tokens Karvon has (refreshed when they expire); a sign-in in the browser only when asked to.
    /// Without tokens and without that, none (the 401 says it needs a sign-in, and no client is registered for nothing).
    /// </summary>
    async Task<ClientOAuthOptions?> OAuthForAsync(McpServerDefinition server, bool interactive, CancellationToken cancellationToken)
    {
        if (signIn is null || Tokens is not { } tokens)
            return null;
        if (interactive)
            // A sign-in starts afresh: its client is registered for this sign-in's redirect.
            tokens.Forget();
        else if (!tokens.HasTokens)
            return null;
        var loopback = new LoopbackSignIn(server.Name, signIn.OpenBrowser, server.OAuthClientId is null ? null : server.OAuthCallbackPort);
        var issuers = await McpDiscovery.IssuersAsync(new Uri(server.Url!), cancellationToken);
        return new ClientOAuthOptions
        {
            RedirectUri = loopback.RedirectUri,
            // A client given out for it (Claude Code's oauth.clientId); otherwise Karvon registers one.
            ClientId = server.OAuthClientId,
            DynamicClientRegistration = server.OAuthClientId is null
                ? new DynamicClientRegistrationOptions { ClientName = "Karvon", ClientUri = new Uri("https://github.com/karvon-studio/karvon") } : null,
            TokenCache = tokens,
            AuthServerSelector = servers => McpDiscovery.Select(servers, issuers),
            AuthorizationCallbackHandler = interactive ? loopback.AuthorizeAsync : (_, _) => throw new SignInNeededException(),
        };
    }

    /// <summary>A server that lets in only the apps it knows says no to Karvon's registration: what to do instead.</summary>
    static string Explain(string message) =>
        message.Contains("Dynamic client registration failed with status Forbidden", StringComparison.Ordinal)
        || message.Contains("Dynamic client registration failed with status Unauthorized", StringComparison.Ordinal)
            ? "it does not let Karvon register to sign in: it lets in only the apps it knows (Claude Code may be one: Sign in with Claude Code), or the client it gave you (oauth.clientId in Claude Code's settings)."
            : message;

    /// <summary>The message, and what lies under it ("Failed to connect transport." says little; "No such file" says why).</summary>
    static string Describe(Exception ex)
    {
        var root = ex.GetBaseException();
        return root == ex || ex.Message.Contains(root.Message, StringComparison.Ordinal) ? ex.Message : $"{ex.Message} {root.Message}";
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
            SetStatus(McpStatus.NotConnected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<McpReply> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        RequestAsync($"tools/call {name} {JsonSerializer.Serialize(arguments)}", "The call failed", async client =>
        {
            var result = await client.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
            Write("←", JsonSerializer.Serialize(result, Pretty));
            var text = result.Content.Count > 0 || result.StructuredContent is null
                ? TextOf(result.Content)
                : JsonSerializer.Serialize(result.StructuredContent, Pretty);
            return result.IsError == true ? new McpReply("The tool reported an error:\n" + text, true) : new McpReply(text);
        });

    public Task<McpReply> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) =>
        RequestAsync($"resources/read {uri}", "The read failed", async client =>
        {
            var result = await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
            Write("←", JsonSerializer.Serialize(result, Pretty));
            return new McpReply(string.Join("\n", result.Contents.Select(c => c is TextResourceContents text ? text.Text : $"({c.MimeType ?? "binary"} content)")));
        });

    public Task<McpReply> GetPromptAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        RequestAsync($"prompts/get {name} {JsonSerializer.Serialize(arguments)}", "The request failed", async client =>
        {
            var result = await client.GetPromptAsync(name, arguments, cancellationToken: cancellationToken);
            Write("←", JsonSerializer.Serialize(result, Pretty));
            return new McpReply(string.Join("\n\n", result.Messages.Select(m => $"{m.Role}: {TextOf([m.Content])}")));
        });

    async Task<McpReply> RequestAsync(string request, string failure, Func<McpClient, Task<McpReply>> send)
    {
        if (_client is not { } client)
            return new McpReply($"{Server.Name} is {StatusText}.", true);
        Write("→", request);
        try
        {
            return await send(client);
        }
        catch (McpException ex)
        {
            Write("error", ex.Message);
            return new McpReply($"{failure}: {ex.Message}", true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ObjectDisposedException)
        {
            // The server went away (it quit, the network dropped): it is not connected any more.
            Write("error", ex.Message);
            await CloseAsync();
            SetStatus(McpStatus.Failed, ex.Message);
            return new McpReply($"{failure}: {ex.Message}", true);
        }
    }

    /// <summary>Text as is; an embedded text resource as its text; anything else as what it is.</summary>
    public static string TextOf(IEnumerable<ContentBlock> blocks) => string.Join("\n", blocks.Select(block => block switch
    {
        TextContentBlock text => text.Text,
        EmbeddedResourceBlock { Resource: TextResourceContents resource } => resource.Text,
        EmbeddedResourceBlock { Resource: var resource } => $"({resource.MimeType ?? "binary"} content of {resource.Uri})",
        ResourceLinkBlock link => $"(a link to {link.Uri})",
        ImageContentBlock image => $"({image.MimeType} image)",
        AudioContentBlock audio => $"({audio.MimeType} audio)",
        _ => $"({block.Type} content)",
    }));

    void SetStatus(McpStatus status, string? error)
    {
        (Status, Error) = (status, error);
        if (status != McpStatus.Connected)
            (Tools, Resources, Prompts) = ([], [], []);
        if (!_disposed)
            Changed?.Invoke();
    }

    /// <summary>A line of the log, with secrets (passwords, tokens) hidden.</summary>
    void Write(string kind, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {kind,-6} {Redaction.Text(text)}";
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > MaxLog)
                _log.RemoveRange(0, 500);
        }
        // A closed connection's last stderr lines go to no page.
        if (!_disposed)
            Logged?.Invoke(line);
    }

    async Task CloseAsync()
    {
        if (_client is not { } client)
            return;
        _client = null;
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CloseAsync();
    }
}
