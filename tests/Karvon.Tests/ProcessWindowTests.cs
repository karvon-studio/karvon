using System.Text.RegularExpressions;

namespace Karvon.Tests;

/// <summary>
/// On Windows a console program Karvon (a windowed app) starts gets a console window of its own unless it is started
/// without one: a run's, a build's, a language server's or a debugger's stood there empty, as their output comes to
/// Karvon. Linux and macOS never show it, so the sources are what is checked here.
/// </summary>
public partial class ProcessWindowTests
{
    static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Karvon.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Karvon.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b")]
    private static partial Regex StartInfo();

    [Fact]
    public void Every_program_Karvon_starts_has_no_console_window_of_its_own()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var missing = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StartInfo().IsMatch(File.ReadAllText(f)))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                // Its own CreateNoWindow, or the helpers that set it (a language server's Launch, a debug adapter's Start).
                return !text.Contains("CreateNoWindow = true", StringComparison.Ordinal) && !text.Contains("Launch(", StringComparison.Ordinal)
                    && !text.Contains("DebugAdapter.Start", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.True(missing.Count == 0, "Started with a console window of its own on Windows: " + string.Join(", ", missing));
        // The helpers themselves start theirs without one.
        foreach (var helper in new[] { Path.Combine("Karvon.Core", "Lsp", "LspClient.cs"), Path.Combine("Karvon.Core", "Debugging", "DebugAdapter.cs") })
            Assert.Contains("CreateNoWindow = true", File.ReadAllText(Path.Combine(src, helper)));
    }
}
