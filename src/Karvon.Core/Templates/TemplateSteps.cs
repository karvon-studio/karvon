using Karvon.Build;
using Karvon.Workspace;

namespace Karvon.Templates;

/// <summary>What a module's template does to make its project: write its files, run its tools (go mod init, cargo init...).</summary>
public static class TemplateSteps
{
    /// <summary>A file of the project, its folders made.</summary>
    public static void Write(string directory, string relativePath, string text)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A tool in the project's folder (found in <paramref name="path"/>), what it says in the log; false when it is not
    /// there or does not succeed, and <paramref name="required"/> makes either an error.
    /// </summary>
    public static async Task<bool> RunAsync(string program, IReadOnlyList<string> args, string directory, string path, Action<string> log, CancellationToken ct, bool required = true)
    {
        if (TaskVariables.Find(program, path, directory) is not { } exe)
        {
            if (required)
                throw new InvalidOperationException($"{program} was not found: it makes this project.");
            log($"{program} was not found: run it yourself ({program} {string.Join(' ', args)}).");
            return false;
        }
        log($"> {program} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        using var runner = ProcessRunner.Start(exe, args, directory, log, new Dictionary<string, string> { ["PATH"] = path });
        var ok = await runner.WaitAsync(ct) == 0;
        if (!ok && required)
            throw new InvalidOperationException($"{program} {string.Join(' ', args)} did not succeed (see the log).");
        return ok;
    }

    /// <summary>A name as the language wants it for a package (lowercase letters, digits and the separator only).</summary>
    public static string PackageName(string name, char separator)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : separator).ToArray();
        var result = new string(chars).Trim(separator);
        return result.Length == 0 || char.IsAsciiDigit(result[0]) ? "app" + separator + result : result;
    }
}
