using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

/// <summary>
/// Packages for Claude, through Karvon: one added to the project (asked first) and the project's restored, by its own
/// package manager (NuGet, npm, pnpm or yarn, pip into the project's virtual environment, cargo, go), with the user's
/// registries and credentials, outside Karvon's sandbox (whose shell reads the package caches but does not change them).
/// npm's, pnpm's and yarn's install scripts do not run: a package's own code would run outside the sandbox.
/// </summary>
public static class PackageClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<string?> workspace) => [new PackageAddTool(workspace), new PackageRestoreTool(workspace)];

    /// <summary>The shell's package installs, which these tools do.</summary>
    public static IEnumerable<ClaudeShellRule> ShellRules =>
    [
        ClaudeShellRule.Command("package install",
            @"(dotnet\s+(add\s+(\S+\s+)?package|restore)|npm\s+(install|i|ci|add)|pnpm\s+(add|install|i)|yarn\s+(add|install)|pip3?\s+install|python3?\s+-m\s+pip\s+install|cargo\s+(add|fetch)|go\s+(get|mod\s+download))\b",
            "Karvon adds and restores packages with your registries and credentials: use mcp__karvon__package_add (asks the user) or "
            + "mcp__karvon__package_restore, not the shell: in Karvon's sandbox the shell reads the package caches but does not change them.")
            with { InWorkspace = true },
    ];

    /// <summary>The package manager of an ecosystem in a folder, and the command that adds <paramref name="name"/> (or restores, when null).</summary>
    internal static (string Program, List<string> Arguments, string? Problem) Command(string ecosystem, string folder, string? name, string? version, string? project)
    {
        switch (ecosystem)
        {
            case "nuget":
                return name is null
                    ? ("dotnet", ["restore", .. Optional(project)], null)
                    : ("dotnet", ["add", .. Optional(project), "package", name, .. version is { Length: > 0 } ? new[] { "--version", version } : []], null);
            case "npm":
                var manager = File.Exists(Path.Combine(folder, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(folder, "yarn.lock")) ? "yarn" : "npm";
                var spec = name is null ? null : version is { Length: > 0 } ? $"{name}@{version}" : name;
                return manager switch
                {
                    "pnpm" => ("pnpm", spec is null ? ["install", "--ignore-scripts"] : ["add", spec, "--ignore-scripts"], null),
                    "yarn" => ("yarn", spec is null ? ["install"] : ["add", spec], null),
                    _ => ("npm", spec is null ? [File.Exists(Path.Combine(folder, "package-lock.json")) ? "ci" : "install", "--ignore-scripts"] : ["install", spec, "--ignore-scripts"], null),
                };
            case "pip":
                if (VirtualEnvironmentPython(folder) is not { } python)
                    return ("", [], "The project has no virtual environment (.venv) yet.");
                if (name is null && !File.Exists(Path.Combine(folder, "requirements.txt")))
                    return ("", [], "The project has no requirements.txt to restore from: add the packages with package_add.");
                return (python, ["-m", "pip", "install", .. name is null ? new[] { "-r", "requirements.txt" } : [version is { Length: > 0 } ? $"{name}=={version}" : name]], null);
            case "cargo":
                return name is null ? ("cargo", ["fetch"], null) : ("cargo", ["add", version is { Length: > 0 } ? $"{name}@{version}" : name], null);
            case "go":
                return name is null ? ("go", ["mod", "download"], null) : ("go", ["get", version is { Length: > 0 } ? $"{name}@{version}" : name], null);
            default:
                return ("", [], "ecosystem is nuget, npm, pip, cargo or go.");
        }
    }

    static string[] Optional(string? value) => value is { Length: > 0 } ? [value] : [];

    /// <summary>The project's virtual environment's python (.venv, venv or env); null when it has none yet.</summary>
    internal static string? VirtualEnvironmentPython(string folder) =>
        new[] { ".venv", "venv", "env" }.Select(v => OperatingSystem.IsWindows() ? Path.Combine(folder, v, "Scripts", "python.exe") : Path.Combine(folder, v, "bin", "python"))
            .FirstOrDefault(File.Exists);

    /// <summary>The ecosystem of a folder, by its files; null when none is there.</summary>
    internal static string? Detect(string folder)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(folder, pattern).Any();
        return Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj") ? "nuget"
            : File.Exists(Path.Combine(folder, "package.json")) ? "npm"
            : File.Exists(Path.Combine(folder, "requirements.txt")) || File.Exists(Path.Combine(folder, "pyproject.toml")) ? "pip"
            : File.Exists(Path.Combine(folder, "Cargo.toml")) ? "cargo"
            : File.Exists(Path.Combine(folder, "go.mod")) ? "go"
            : null;
    }

    /// <summary>Runs a package manager in the folder: what it printed, and whether it failed.</summary>
    internal static async Task<ClaudeToolOutput> RunAsync(string program, IReadOnlyList<string> arguments, string folder, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = start.RedirectStandardError = start.RedirectStandardInput = true;
        // A package's install scripts do not run (yarn's and npm's own setting besides the flag).
        start.Environment["npm_config_ignore_scripts"] = "true";
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ClaudeToolOutput.Error($"{program} is not installed (or not on the PATH).");
        }
        using var running = process ?? throw new InvalidOperationException($"{program} did not start.");
        running.StandardInput.Close();
        var output = new StringBuilder();
        void Add(string? line)
        {
            if (line is not null)
                lock (output)
                    if (output.Length < 30_000)
                        output.AppendLine(line);
        }
        running.OutputDataReceived += (_, e) => Add(e.Data);
        running.ErrorDataReceived += (_, e) => Add(e.Data);
        running.BeginOutputReadLine();
        running.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await running.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                running.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return ClaudeToolOutput.Error(output + "\n(Stopped: it ran for ten minutes.)");
        }
        await running.WaitForExitAsync(CancellationToken.None);
        var text = output.ToString().TrimEnd();
        var line = $"{program} {string.Join(' ', arguments)}";
        return running.ExitCode == 0 ? new ClaudeToolOutput($"{line}: done.\n{Tail(text, 6000)}".TrimEnd())
            : ClaudeToolOutput.Error($"{line} failed (exit code {running.ExitCode}):\n{Tail(text, 12000)}".TrimEnd());
    }
}

abstract class PackageTool(Func<string?> workspace) : IClaudeTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public string? Refusal(JsonElement input) => workspace() is null ? "No folder is open in Karvon." : null;

    /// <summary>The folder the package manager runs in: the project's (folder), else the workspace.</summary>
    protected string? Folder(JsonElement input) =>
        workspace() is not { } root ? null : Str(input, "folder") is { Length: > 0 } folder ? Path.GetFullPath(folder, root) : root;

    public abstract Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken);

    protected const string Properties = """
        "ecosystem":{"type":"string","enum":["nuget","npm","pip","cargo","go"],"description":"Found from the folder's files when not given"},
        "folder":{"type":"string","description":"The project's folder, relative to the workspace (the workspace when not given)"},
        "project":{"type":"string","description":"NuGet: the .csproj (or .sln to restore), relative to the folder"}
        """;
}

/// <summary><c>package_add</c>: one package added to the project by its package manager; asked first.</summary>
sealed class PackageAddTool(Func<string?> workspace) : PackageTool(workspace)
{
    public override string Name => "package_add";

    public override string Description =>
        "Adds a package to the project with its package manager (dotnet add package, npm/pnpm/yarn add, pip install into the project's "
        + ".venv, made when it has none, cargo add, go get), from the user's registries with their credentials. Install scripts do not "
        + "run. The user is asked first.";

    public override JsonObject InputSchema => Schema($$"""
        {"type":"object","properties":{
          "name":{"type":"string","description":"The package"},
          "version":{"type":"string","description":"Its version (the latest when not given)"},
          {{Properties}}},
         "required":["name"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var name = Str(input, "name");
        var version = Str(input, "version");
        return new(ToolAccess.Write, "Add package", $"{name}{(version is { Length: > 0 } ? " " + version : "")}", $"Add the package {name} to the project?")
        {
            Warning = "It is downloaded from your package registry and becomes part of the project's build.",
        };
    }

    public override async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Folder(input) is not { } folder || !Directory.Exists(folder))
            return ClaudeToolOutput.Error("The folder is not there.");
        if (Str(input, "name") is not { Length: > 0 } name)
            return ClaudeToolOutput.Error("Give the package's name.");
        if ((Str(input, "ecosystem") ?? PackageClaudeTools.Detect(folder)) is not { } ecosystem)
            return ClaudeToolOutput.Error("No project is in the folder (a .csproj, package.json, requirements.txt or pyproject.toml, Cargo.toml, go.mod): give folder or ecosystem.");
        // pip: the project's own environment, made first when it has none.
        if (ecosystem == "pip" && PackageClaudeTools.VirtualEnvironmentPython(folder) is null)
        {
            var made = await PackageClaudeTools.RunAsync(OperatingSystem.IsWindows() ? "python" : "python3", ["-m", "venv", ".venv"], folder, cancellationToken);
            if (made.IsError)
                return made;
        }
        var (program, arguments, problem) = PackageClaudeTools.Command(ecosystem, folder, name, Str(input, "version"), Str(input, "project"));
        return problem is not null ? ClaudeToolOutput.Error(problem) : await PackageClaudeTools.RunAsync(program, arguments, folder, cancellationToken);
    }
}

/// <summary><c>package_restore</c>: the project's packages restored by its package manager.</summary>
sealed class PackageRestoreTool(Func<string?> workspace) : PackageTool(workspace)
{
    public override string Name => "package_restore";

    public override string Description =>
        "Restores the project's packages with its package manager (dotnet restore, npm ci or install, pnpm/yarn install, pip install -r "
        + "requirements.txt into the project's .venv, cargo fetch, go mod download), from the user's registries with their credentials. "
        + "Install scripts do not run.";

    public override JsonObject InputSchema => Schema("{\"type\":\"object\",\"properties\":{" + Properties + "}}");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Restore packages", Str(input, "folder"), "");

    public override async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Folder(input) is not { } folder || !Directory.Exists(folder))
            return ClaudeToolOutput.Error("The folder is not there.");
        if ((Str(input, "ecosystem") ?? PackageClaudeTools.Detect(folder)) is not { } ecosystem)
            return ClaudeToolOutput.Error("No project is in the folder (a .csproj, package.json, requirements.txt or pyproject.toml, Cargo.toml, go.mod): give folder or ecosystem.");
        if (ecosystem == "pip" && PackageClaudeTools.VirtualEnvironmentPython(folder) is null)
        {
            var made = await PackageClaudeTools.RunAsync(OperatingSystem.IsWindows() ? "python" : "python3", ["-m", "venv", ".venv"], folder, cancellationToken);
            if (made.IsError)
                return made;
        }
        var (program, arguments, problem) = PackageClaudeTools.Command(ecosystem, folder, null, null, Str(input, "project"));
        return problem is not null ? ClaudeToolOutput.Error(problem) : await PackageClaudeTools.RunAsync(program, arguments, folder, cancellationToken);
    }
}
