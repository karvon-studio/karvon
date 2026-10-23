using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Services;
using Karvon.Workspace;

namespace Karvon.TypeScript;

/// <summary>
/// ESLint's language server, the one of VS Code's ESLint extension (MIT), from Open VSX: checked against the hash pinned
/// here, into ~/.local/share/karvon/eslint/&lt;version&gt;. It runs on Node.js and loads the folder's own ESLint
/// (node_modules/eslint) with its configuration and plugins.
/// </summary>
public static class EslintLocator
{
    public const string Version = "3.0.34";
    const string Sha256 = "ca5334d46f6a39079e751ef4601bfc9f86bc3a46483e87291ec609239d161308";

    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "eslint", Version);

    /// <summary>The server's script, or null when it is not installed.</summary>
    public static string? Find()
    {
        var server = Path.Combine(InstallRoot, "extension", "server", "out", "eslintServer.js");
        return File.Exists(server) ? server : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is not null)
            return InstallRoot;
        var url = $"https://open-vsx.org/api/dbaeumer/vscode-eslint/{Version}/file/dbaeumer.vscode-eslint-{Version}.vsix";
        log($"Downloading {url} ...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var vsix = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(vsix));
        if (hash != Sha256)
            throw new InvalidOperationException($"{url} checksum mismatch ({hash}).");
        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(vsix));
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0
                && (e.FullName.StartsWith("extension/server/out/", StringComparison.Ordinal) || e.FullName == "extension/License.txt")))
            {
                var target = Path.Combine(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
            Directory.Move(staging, InstallRoot);
        }
        catch (IOException) when (Find() is not null)
        {
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        log($"ESLint's language server {Version} installed into {InstallRoot}");
        return Find() is not null ? InstallRoot : throw new InvalidOperationException("ESLint's language server was installed but is not there.");
    }
}

/// <summary>
/// ESLint's language server for a workspace: its diagnostics on the JavaScript and TypeScript files, beside TypeScript's
/// server, by the folder's own ESLint and configuration (eslint.config.js, or an .eslintrc).
/// </summary>
public sealed class EslintServer : LspClient
{
    readonly string _root;
    readonly Action<string>? _openUrl;

    EslintServer(Process process, JsonRpcConnection rpc, string root, Action<string>? openUrl) : base(process, rpc, root) => (_root, _openUrl) = (root, openUrl);

    /// <summary>The files it checks: TypeScript's server's (.js, .jsx, .ts, .tsx and their module kinds).</summary>
    public static bool Serves(string path) => TypeScriptServer.Serves(path);

    public override string LanguageIdFor(string path) => TypeScriptServer.LanguageIdOf(path);

    /// <summary>
    /// The folder's own ESLint for a file: node_modules/eslint in its folder or one above it, up to the workspace's; null
    /// with none (the server would find none to run either).
    /// </summary>
    public static string? LibraryFor(string file, string workspace)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var start = Path.GetFullPath(file);
        for (var dir = Directory.Exists(start) ? start : Path.GetDirectoryName(start); dir is not null && FilePaths.IsSameOrUnder(dir, root); dir = Path.GetDirectoryName(dir))
        {
            var library = Path.Combine(dir, "node_modules", "eslint");
            if (File.Exists(Path.Combine(library, "package.json")))
                return library;
        }
        return null;
    }

    /// <summary>Its settings, VS Code's ESLint extension's defaults: it checks as one types, in the workspace's folder.</summary>
    protected override object? ConfigurationFor(string? section) => section is null or "" ? new
    {
        validate = "on",
        run = "onType",
        workspaceFolder = new { name = Path.GetFileName(_root), uri = ToUri(_root) },
        workingDirectory = new { mode = "location" },
        problems = new { shortenToSingleLine = false },
        codeAction = new
        {
            disableRuleComment = new { enable = true, location = "separateLine", commentStyle = "line" },
            showDocumentation = new { enable = true },
        },
    } : null;

    /// <summary>What it asks of the editor (the folder has no ESLint, a file no configuration...): said in Output, answered.</summary>
    protected override void WireServer()
    {
        Rpc.OnRequest("eslint/noLibrary", _ =>
        {
            OnLog("ESLint: the folder has no ESLint of its own (npm install --save-dev eslint).");
            return Task.FromResult<object?>(new { });
        });
        Rpc.OnRequest("eslint/noConfig", p =>
        {
            OnLog($"ESLint: no configuration for {(p.TryGetProperty("document", out var d) && d.TryGetProperty("uri", out var u) ? u.GetString() : "a file")}.");
            return Task.FromResult<object?>(new { });
        });
        Rpc.OnRequest("eslint/probeFailed", _ => Task.FromResult<object?>(new { }));
        // Show documentation for a rule: its page in the browser.
        Rpc.OnRequest("eslint/openDoc", p =>
        {
            if (p.TryGetProperty("url", out var url) && url.GetString() is { } page && Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                _openUrl?.Invoke(page);
            return Task.FromResult<object?>(new { });
        });
        Rpc.OnNotification("eslint/status", _ => { });
        Rpc.OnNotification("eslint/exitCalled", _ => OnLog("ESLint: a plugin or the configuration called process.exit."));
    }

    /// <param name="openUrl">Opens a rule's documentation in the browser.</param>
    public static async Task<LspClient> StartAsync(LanguageServerStart start, Action<string>? openUrl = null)
    {
        await EslintLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        var server = EslintLocator.Find() ?? throw new InvalidOperationException("ESLint's language server is not installed.");
        var path = string.Join(Path.PathSeparator, new[] { Build.NodeLocator.DirectoryOffPath(), Environment.GetEnvironmentVariable("PATH") }.OfType<string>());
        var node = TaskVariables.Find("node", path, start.Workspace) ?? throw new InvalidOperationException("Node.js was not found: ESLint runs on it.");
        var (process, rpc) = Launch(new ProcessStartInfo(node, [server, "--stdio"]) { WorkingDirectory = start.Workspace });
        var client = new EslintServer(process, rpc, start.Workspace, openUrl);
        try
        {
            await client.InitializeAsync(start.Workspace, null, start.Cancellation);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }
}
