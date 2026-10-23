namespace Karvon.Modules;

/// <summary>What Build, Rebuild, Clean, Pack and Publish ask of the module whose solution is open.</summary>
/// <param name="Verb">"build", "rebuild", "clean", "pack" or "publish".</param>
/// <param name="Target">The solution's file, or the project's the command is for.</param>
/// <param name="Configuration">The build configuration chosen on the toolbar ("Debug", "Release").</param>
public sealed record BuildRequest(string Verb, string Target, string Configuration);

/// <summary>How a module builds a solution: the program it runs in the solution's folder, and what its output says.</summary>
public sealed record BuildCommand(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>The error or warning a line of its output says (MSBuild's format, as VS Code's $msCompile reads it).</summary>
    public Func<string, Build.BuildDiagnostic?> Problems { get; init; } = Build.BuildOutputParser.Parse;

    /// <summary>Whether a line says a project was built ("Web -> /out/Web.dll"): the "project(s) built" of the build's last line.</summary>
    public Func<string, bool>? ProjectBuilt { get; init; }
}
