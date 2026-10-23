using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.TypeScript;

/// <summary>
/// HTML's, CSS's and JSON's language servers, VS Code's own (MIT): its html-, css- and json-language-features
/// extensions from Open VSX, each server bundled in it, with the TypeScript library the HTML server's script blocks
/// need; checked against the hashes pinned here, into ~/.local/share/karvon/web-languages/&lt;version&gt;. They run on
/// Node.js and read no workspace code.
/// </summary>
public static class WebLanguagesLocator
{
    public const string Version = "1.95.3";
    const string TypeScriptLibrary = "5.9.3";

    static readonly (string Name, string Sha256)[] Extensions =
    [
        ("html", "1968c387c20036da0636c45a00863d4a4be980033870bfde864e6e097b033b12"),
        ("css", "d4352f98bcffb5f27f4efabe797f9ce78042ec07a4037113c21e9d41348ed114"),
        ("json", "8436d6ffe294bfb93d517ca37c4913a40bb0e035883c7b6aed9daaf1a32f6b80"),
    ];

    const string TypeScriptSha256 = "10e108c9cf7d5f2879053dff18515fb405abf2ccef63eaaf017d9c571687a1d3";

    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "web-languages", Version);

    /// <summary>A language's server (html, css, json), or null when it is not installed.</summary>
    public static string? Find(string language)
    {
        var server = Path.Combine(InstallRoot, language, "extension", "server", "dist", "node", $"{language}ServerMain.js");
        return File.Exists(server) && Directory.Exists(Path.Combine(InstallRoot, "node_modules", "typescript")) ? server : null;
    }

    public static bool IsInstalled() => Extensions.All(e => Find(e.Name) is not null);

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (IsInstalled())
            return InstallRoot;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        async Task<byte[]> DownloadAsync(string url, string sha256)
        {
            log($"Downloading {url} ...");
            var bytes = await http.GetByteArrayAsync(url, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return hash == sha256 ? bytes : throw new InvalidOperationException($"{url} checksum mismatch ({hash}).");
        }
        // Another Karvon may be installing them at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, sha256) in Extensions)
            {
                var vsix = await DownloadAsync($"https://open-vsx.org/api/vscode/{name}-language-features/{Version}/file/vscode.{name}-language-features-{Version}.vsix", sha256);
                using var archive = new ZipArchive(new MemoryStream(vsix));
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("extension/server/dist/node/", StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    var target = Path.Combine(staging, name, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }
            // The HTML server's <script> blocks: the TypeScript library (its JavaScript API), where Node finds it from there.
            var typescript = await DownloadAsync($"https://registry.npmjs.org/typescript/-/typescript-{TypeScriptLibrary}.tgz", TypeScriptSha256);
            var unpacked = Path.Combine(staging, "typescript-package");
            Directory.CreateDirectory(unpacked);
            await using (var gzip = new GZipStream(new MemoryStream(typescript), CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, unpacked, overwriteFiles: true, ct);
            Directory.CreateDirectory(Path.Combine(staging, "node_modules"));
            Directory.Move(Path.Combine(unpacked, "package"), Path.Combine(staging, "node_modules", "typescript"));
            Directory.Delete(unpacked, recursive: true);
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
            Directory.Move(staging, InstallRoot);
        }
        catch (IOException) when (IsInstalled())
        {
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        log($"The HTML, CSS and JSON language servers {Version} installed into {InstallRoot}");
        return IsInstalled() ? InstallRoot : throw new InvalidOperationException("The HTML, CSS and JSON language servers were installed but are not there.");
    }
}

/// <summary>One of VS Code's HTML, CSS and JSON language servers, on Node.js, for a workspace's files of its language.</summary>
public sealed class WebLanguageServer : LspClient
{
    readonly string _language;

    WebLanguageServer(Process process, JsonRpcConnection rpc, string root, string language) : base(process, rpc, root) => _language = language;

    /// <summary>A file's language, and so its server: html, css (with scss and less) or json; null for another file.</summary>
    public static string? LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "html",
        ".css" or ".scss" or ".less" => "css",
        ".json" or ".jsonc" or ".code-workspace" => "json",
        _ => null,
    };

    /// <summary>The LSP language id: VS Code's, with comments in JSON where VS Code allows them (jsonc).</summary>
    public static string LanguageIdOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();
        return LanguageOf(path) switch
        {
            "css" => extension.TrimStart('.'),
            "json" when extension is ".jsonc" or ".code-workspace" || name.StartsWith("tsconfig", StringComparison.Ordinal) || name.StartsWith("jsconfig", StringComparison.Ordinal)
                || Path.GetFileName(Path.GetDirectoryName(path)) == ".vscode" => "jsonc",
            { } language => language,
            null => "plaintext",
        };
    }

    public override string LanguageIdFor(string path) => LanguageOf(path) == _language ? LanguageIdOf(path) : "plaintext";

    /// <summary>
    /// The settings VS Code gives them, its defaults: they read them as objects that are there (the CSS server's checks
    /// fail on none).
    /// </summary>
    protected override object? ConfigurationFor(string? section) => section switch
    {
        "css" or "scss" or "less" => new
        {
            validate = true,
            lint = new { },
            completion = new { triggerPropertyValueCompletion = true, completePropertyWithSemicolon = true },
            hover = new { documentation = true, references = true },
            format = new { enable = true },
        },
        "html" => new
        {
            validate = new { scripts = true, styles = true },
            format = new { enable = true },
            suggest = new { html5 = true },
            hover = new { documentation = true, references = true },
            autoClosingTags = true,
        },
        "javascript" => new { validate = new { enable = true }, format = new { enable = true } },
        "json" => new { validate = new { enable = true }, format = new { enable = true }, schemas = Array.Empty<object>() },
        "http" => new { proxy = "", proxyStrictSSL = true },
        _ => null,
    };

    public static async Task<LspClient> StartAsync(string language, LanguageServerStart start)
    {
        await WebLanguagesLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        var server = WebLanguagesLocator.Find(language) ?? throw new InvalidOperationException($"The {language} language server is not installed.");
        var path = string.Join(Path.PathSeparator, new[] { Build.NodeLocator.DirectoryOffPath(), Environment.GetEnvironmentVariable("PATH") }.OfType<string>());
        var node = TaskVariables.Find("node", path, start.Workspace) ?? throw new InvalidOperationException("Node.js was not found: the HTML, CSS and JSON language servers run on it.");
        var (process, rpc) = Launch(new ProcessStartInfo(node, [server, "--stdio"]) { WorkingDirectory = start.Workspace });
        var client = new WebLanguageServer(process, rpc, start.Workspace, language);
        try
        {
            // As VS Code's extensions start them: formatting by the server, HTML's style and script blocks too.
            await client.InitializeAsync(start.Workspace, language switch
            {
                "html" => new { provideFormatter = true, embeddedLanguages = new { css = true, javascript = true }, handledSchemas = new[] { "file" } },
                "css" => (object)new { provideFormatter = true, handledSchemas = new[] { "file" } },
                _ => new { provideFormatter = true, handledSchemaProtocols = new[] { "file" } },
            }, start.Cancellation);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }
}
