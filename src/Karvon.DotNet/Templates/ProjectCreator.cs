using Karvon.Build;
using Karvon.Templates;

namespace Karvon.DotNet.Templates;

/// <summary>Creates a project from a template with <c>dotnet new</c> and puts it in a solution, like VS's new project wizard.</summary>
public static class ProjectCreator
{
    static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".esproj"];

    /// <summary>Runs the template and returns the solution to open.</summary>
    public static async Task<string> CreateAsync(NewProjectRequest request, Action<string> log, CancellationToken ct = default, bool restore = true)
    {
        Directory.CreateDirectory(request.Location);
        await RunAsync(TemplateArguments(request, restore), request.Location, log, ct);

        // Multi-project templates (e.g. Blazor with WebAssembly) bring their own solution.
        var templateSolution = Solutions(request.ProjectDirectory).FirstOrDefault();
        if (request.SolutionInSameDirectory && templateSolution is not null)
            return templateSolution;

        var solutionName = request.SolutionInSameDirectory ? request.ProjectName : request.SolutionName;
        await RunAsync(["new", "sln", "--name", solutionName, "--output", request.SolutionDirectory], request.Location, log, ct);
        var solution = Solutions(request.SolutionDirectory).FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == solutionName)
            ?? throw new InvalidOperationException("dotnet new sln did not create a solution file.");
        if (templateSolution is not null && templateSolution != solution)
            File.Delete(templateSolution);

        var projects = ProjectsIn(request.ProjectDirectory);
        if (projects.Count > 0)
            await RunAsync(["sln", solution, "add", "--in-root", .. projects], request.SolutionDirectory, log, ct);
        return solution;
    }

    /// <summary>
    /// Add > New Project: runs the template into a folder of an open solution and returns the project files it made, for
    /// the solution to add. A multi-project template's own solution is not kept: its projects go into the open one.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CreateProjectAsync(NewProjectRequest request, Action<string> log, CancellationToken ct = default, bool restore = true)
    {
        Directory.CreateDirectory(request.Location);
        await RunAsync(TemplateArguments(request, restore), request.Location, log, ct);
        foreach (var solution in Solutions(request.ProjectDirectory).ToList())
            File.Delete(solution);
        var projects = ProjectsIn(request.ProjectDirectory);
        return projects.Count > 0 ? projects : throw new InvalidOperationException("The template did not make a project file.");
    }

    static List<string> TemplateArguments(NewProjectRequest request, bool restore)
    {
        List<string> args = ["new", request.Template.ShortName, "--name", request.ProjectName, "--output", request.ProjectDirectory];
        if (!restore)
            args.Add("--no-restore");
        if (request.Template.Language is { } language)
            args.AddRange(["--language", language]);
        foreach (var (option, value) in request.Options)
            args.AddRange(option.Arguments(value));
        return args;
    }

    static List<string> ProjectsIn(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();

    static IEnumerable<string> Solutions(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [];

    static async Task RunAsync(IReadOnlyList<string> args, string workingDirectory, Action<string> log, CancellationToken ct)
    {
        log("> dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, workingDirectory, line =>
        {
            lock (lines)
                lines.Add(line);
            log(line);
        });
        if (await runner.WaitAsync(ct) != 0)
        {
            lock (lines)
                throw new InvalidOperationException(lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"dotnet {args[0]} {args[1]} failed.");
        }
    }
}
