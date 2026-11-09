using Karvon.Workspace;

namespace Karvon.Arche;

/// <summary>
/// The Arche the user has: its tools on the PATH, or a build of arche-lang at ARCHE_HOME or ~/.arche. Never the open folder's: a
/// folder that holds a compiler is not to be run for having been opened. A package's manifest and build file are read here too.
/// </summary>
public static class ArcheTools
{
    public const string Manifest = "package.arche";

    public const string BuildFile = "build.arche";

    public const string Compiler = "arche";

    public const string Driver = "arche-driver";

    public const string Server = "arche-lsp";

    /// <summary>The script that builds a file's test blocks and runs them; sh runs it, as Windows starts no script.</summary>
    public const string TestRunner = "arche-test";

    public const string NotFound =
        "Arche was not found: build arche-lang (make -C compiler, then make -C driver), then put compiler/build and driver/build on the PATH, or set ARCHE_HOME to it, or link it as ~/.arche.";

    /// <summary>Where a tool is in a build of arche-lang.</summary>
    static string FolderInRepository(string tool) => tool switch
    {
        Compiler => Path.Combine("compiler", "build"),
        TestRunner => "compiler",
        _ => Path.Combine("driver", "build"),
    };

    /// <summary>A build of arche-lang: ARCHE_HOME, or else ~/.arche.</summary>
    static string Home(string? home) =>
        home ?? (Environment.GetEnvironmentVariable("ARCHE_HOME") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arche"));

    /// <summary>A tool on the PATH. arche-lang's builds have no extension, on Windows too, where PATHEXT would not find them.</summary>
    static string? OnPath(string tool, string path)
    {
        if (TaskVariables.Find(tool, path, "/") is { } found)
            return found;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
    }

    public static string? Find(string tool, string path, string? home = null)
    {
        if (OnPath(tool, path) is { } found)
            return found;
        var inHome = Path.Combine(Home(home), FolderInRepository(tool), tool);
        if (File.Exists(inHome))
            return inHome;
        // arche-test is no build: it sits above the compiler that is on the PATH.
        if (tool == TestRunner && OnPath(Compiler, path) is { } compiler && Path.GetDirectoryName(Path.GetDirectoryName(compiler)) is { } above)
        {
            var beside = Path.Combine(above, tool);
            if (File.Exists(beside))
                return beside;
        }
        return null;
    }

    /// <summary>The folders of the tools in a build of arche-lang that the PATH does not have.</summary>
    public static IReadOnlyList<string> DirectoriesOffPath(string path, string? home = null)
    {
        List<string> folders = [];
        foreach (var tool in new[] { Compiler, Driver, Server })
            if (OnPath(tool, path) is null && Find(tool, path, home) is { } found && Path.GetDirectoryName(found) is { } folder && !folders.Contains(folder))
                folders.Add(folder);
        return folders;
    }

    /// <summary>The PATH Arche's tools run with: their folders first when it has them not.</summary>
    public static string PathWithTools(string path, string? home = null) =>
        string.Join(Path.PathSeparator, [.. DirectoriesOffPath(path, home), path]);

    /// <summary>What Arche's tools need beyond the PATH: which compiler to run, since they may be apart from it.</summary>
    public static IReadOnlyDictionary<string, string>? ToolEnvironment(string path, string? home = null) =>
        Find(Compiler, path, home) is { } compiler ? new Dictionary<string, string> { ["ARCHE"] = compiler } : null;
}
