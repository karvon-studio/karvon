using System.Diagnostics;
using System.Text;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Jwc;

/// <summary>
/// jwc lsp, the compiler's language server, for a workspace's .jwc files: their errors as they are typed, hover (a query's SQL
/// too), go to definition, completion and parameter info. It does not format, so Format Document is jwc fmt's.
/// </summary>
public sealed class JwcServer : LspClient
{
    readonly string _jwc;

    JwcServer(Process process, JsonRpcConnection rpc, string root, string jwc) : base(process, rpc, root) => _jwc = jwc;

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".jwc", StringComparison.OrdinalIgnoreCase);

    public override string LanguageIdFor(string path) => Serves(path) ? "jwc" : "plaintext";

    /// <summary>Starts it on stdio in the workspace.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var jwc = JwcTools.Find(Environment.GetEnvironmentVariable("PATH") ?? "") ?? throw new InvalidOperationException(JwcTools.NotFound);
        var psi = new ProcessStartInfo(jwc) { WorkingDirectory = start.Workspace };
        psi.ArgumentList.Add("lsp");
        var (process, rpc) = Launch(psi);
        var server = new JwcServer(process, rpc, start.Workspace, jwc);
        try
        {
            await server.InitializeAsync(start.Workspace, null, start.Cancellation);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }

    /// <summary>
    /// The editor's text through jwc fmt, from a copy of its own (fmt reads files, and this text may not be saved): what it
    /// changes as one edit. A file that does not parse, or that fmt would lose a comment of, stays as it is, and the status
    /// bar says why.
    /// </summary>
    public override async Task<IReadOnlyList<LspTextEdit>> FormatAsync(string path, string text, int tabSize, bool insertSpaces, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("karvon-jwc-").FullName;
        try
        {
            var file = Path.GetFileName(path);
            await File.WriteAllTextAsync(Path.Combine(dir, file), text, new UTF8Encoding(false), ct);
            var psi = new ProcessStartInfo(_jwc)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "fmt", "--stdout", file })
                psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi) ?? throw new IOException("jwc fmt did not start.");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new IOException("jwc fmt: " + (await errors).Split('\n', 2)[0].Trim());
            return EditsFrom(text, await output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The edit that turns a text into its formatted form, by whole lines: what they start and end with in common stays, so
    /// the caret and the undo stack stay with it. The text's line endings are kept.
    /// </summary>
    internal static IReadOnlyList<LspTextEdit> EditsFrom(string text, string formatted)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        formatted = formatted.ReplaceLineEndings(newLine);
        if (formatted == text)
            return [];
        var (before, after) = (text.Split('\n'), formatted.Split('\n'));
        // The last line of the shorter one is always in the edit: what is added or dropped at the end has a place then.
        var first = 0;
        while (first < Math.Min(before.Length, after.Length) - 1 && before[first] == after[first])
            first++;
        var last = 0;
        while (last < before.Length - first && last < after.Length - first && before[^(last + 1)] == after[^(last + 1)])
            last++;
        var end = last > 0 ? new LspPosition(before.Length - last, 0) : new LspPosition(before.Length - 1, before[^1].Length);
        var lines = after[first..^last];
        var replacement = lines.Length == 0 ? "" : string.Join('\n', lines) + (last > 0 ? "\n" : "");
        return [new LspTextEdit(new LspRange(new LspPosition(first, 0), end), replacement)];
    }
}
