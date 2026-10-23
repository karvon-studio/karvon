using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Modules;

namespace Karvon.Kotlin;

/// <summary>
/// A Kotlin Gradle application's run (a build.gradle.kts or build.gradle with the Kotlin plugin and an application's main
/// class): Gradle's run, which F5 debugs under kotlin-lsp's debugger (it builds and runs the program itself).
/// </summary>
public static partial class KotlinTasks
{
    public const string Source = "kotlin";

    [GeneratedRegex(@"mainClass(?:Name)?\s*(?:=|\.set\()\s*[""']([^""']+)[""']")]
    private static partial Regex MainClass();

    /// <summary>The build file's application main class, of a Kotlin build (null for another, or one with none).</summary>
    public static string? MainClassOf(string workspace)
    {
        foreach (var name in new[] { "build.gradle.kts", "build.gradle" })
        {
            var file = Path.Combine(workspace, name);
            if (!File.Exists(file))
                continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("kotlin", StringComparison.Ordinal))
                return null;
            return MainClass().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        }
        return null;
    }

    /// <summary>
    /// The source file of a main class (MainKt: src/main/kotlin/Main.kt, in its package's folder; else the file with a
    /// fun main), which kotlin-lsp knows the module to build and run by; null when there is none.
    /// </summary>
    public static string? MainFileOf(string workspace, string mainClass)
    {
        var sources = Path.Combine(workspace, "src", "main", "kotlin");
        if (!Directory.Exists(sources))
            return null;
        var dot = mainClass.LastIndexOf('.');
        var (package, name) = dot < 0 ? ("", mainClass) : (mainClass[..dot], mainClass[(dot + 1)..]);
        if (name.EndsWith("Kt", StringComparison.Ordinal)
            && Path.Combine(sources, package.Replace('.', Path.DirectorySeparatorChar), name[..^2] + ".kt") is var file && File.Exists(file))
            return file;
        return Directory.EnumerateFiles(sources, "*.kt", SearchOption.AllDirectories)
            .FirstOrDefault(f => File.ReadAllText(f).Contains("fun main(", StringComparison.Ordinal));
    }

    /// <summary>The build's Gradle: its wrapper (gradlew, gradlew.bat on Windows), else the PATH's gradle.</summary>
    public static string GradleOf(string workspace) =>
        Path.Combine(workspace, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew") is var wrapper && File.Exists(wrapper) ? wrapper : "gradle";

    /// <summary>Whether the folder's Gradle build is Kotlin's (its build file uses the Kotlin plugin).</summary>
    public static bool IsKotlinBuild(string workspace) =>
        new[] { "build.gradle.kts", "build.gradle" }.Select(n => Path.Combine(workspace, n)).FirstOrDefault(File.Exists) is { } file
        && File.ReadAllText(file).Contains("kotlin", StringComparison.Ordinal);

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (MainClassOf(workspace) is not { } main)
            return [];
        var debug = new JsonObject
        {
            ["type"] = "intellij_gradle",
            ["request"] = "launch",
            ["name"] = $"kotlin: {main}",
            ["mainClass"] = main,
            ["cwd"] = workspace,
        };
        if (MainFileOf(workspace, main) is { } file)
            debug["file"] = file;
        return
        [
            new($"kotlin: run {main}", WorkspaceTaskKind.Run, GradleOf(workspace), ["run", "-q"], workspace)
            {
                Source = Source,
                IsDefault = true,
                Debug = new DebugLaunch($"kotlin: {main}", debug) { Source = Source },
            },
        ];
    }
}
