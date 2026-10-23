using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.Debugging;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.DotNet.Debugging;

/// <summary>
/// netcoredbg, .NET's debugger: "coreclr" configurations as VS Code's C# extension has them (a program, or an attach to
/// a process), and "dotnet" ones, which name a project and its launch profile as Visual Studio starts one (F5 on the
/// startup project). A "dotnet" configuration becomes a "coreclr" one here: the project's output assembly under
/// <c>dotnet</c>, in the project's folder, with the profile's environment, arguments and browser. It does not build: F5
/// builds the project first, and a launch.json's says so with its preLaunchTask.
/// </summary>
static class DotNetDebugger
{
    public static DebuggerInfo Info(IWorkbench workbench) =>
        new("netcoredbg", $"netcoredbg {NetcoredbgLocator.Version}", ["coreclr", "dotnet"], async start =>
        {
            if (start.Configuration["type"]?.GetValue<string>() == "dotnet")
                await ResolveAsync(start.Configuration, workbench, start.Log);
            return DebugSession.Netcoredbg(await NetcoredbgLocator.EnsureInstalledAsync(start.Log, start.Cancellation));
        });

    /// <summary>
    /// What a .NET program writes, as a terminal shows it: its colors kept when its output is redirected, and none of
    /// the Debug logger's lines (the Output window has them already).
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> OutputEnvironment = new Dictionary<string, string>
    {
        ["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1",
        ["Logging__Debug__LogLevel__Default"] = "None",
    };

    /// <summary>A "dotnet" configuration ("projectPath", "launchProfile", "configuration", "args", "env") as the "coreclr" launch it is.</summary>
    internal static async Task ResolveAsync(JsonObject configuration, IWorkbench workbench, Action<string> log)
    {
        var path = configuration["projectPath"]?.GetValue<string>() is { Length: > 0 } p ? Path.GetFullPath(p)
            : throw new InvalidOperationException("A \"dotnet\" configuration names its project in \"projectPath\".");
        var project = workbench.Solution?.Projects.FirstOrDefault(x => x.FilePath == path)
            ?? (File.Exists(path) ? ProjectInfo.Load(Path.GetFileNameWithoutExtension(path), path) : throw new InvalidOperationException($"{path} was not found."));
        var buildConfiguration = configuration["configuration"]?.GetValue<string>() ?? workbench.Configuration;
        var program = await DotNetProjects.TargetPathAsync(project, buildConfiguration);
        if (program is null || !File.Exists(program))
            throw new InvalidOperationException($"the output assembly of {project.Name} was not found: build it first.");
        var profileName = configuration["launchProfile"]?.GetValue<string>();
        var profile = profileName is null ? null : project.LaunchProfiles.FirstOrDefault(x => x.Name == profileName);
        if (profileName is not null && profile is null)
            log($"{project.Name} has no launch profile '{profileName}': it starts without one.");

        var env = new JsonObject();
        foreach (var (name, value) in profile?.EnvironmentVariables ?? new Dictionary<string, string>())
            env[name] = value;
        // dotnet run turns applicationUrl into ASPNETCORE_URLS; launching the dll directly must do the same.
        if (profile?.ApplicationUrl is { Length: > 0 } urls && env["ASPNETCORE_URLS"] is null)
            env["ASPNETCORE_URLS"] = urls;
        foreach (var (name, value) in OutputEnvironment)
            env[name] ??= value;
        if (configuration["env"] is JsonObject own)
            foreach (var (name, value) in own)
                env[name] = value?.DeepClone();

        var args = new JsonArray(program);
        if (profile?.CommandLineArgs is { Length: > 0 } extra)
            foreach (var arg in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(arg);
        if (configuration["args"] is JsonArray more)
            foreach (var arg in more)
                args.Add(arg?.DeepClone());

        configuration["type"] = "coreclr";
        configuration["program"] = DotnetCli.Executable;
        configuration["args"] = args;
        configuration["cwd"] ??= project.Directory;
        configuration["env"] = env;
        configuration["justMyCode"] ??= true;
        configuration["enableStepFiltering"] ??= true;
        configuration["console"] ??= "internalConsole";
        // Visual Studio's launchBrowser: the browser opens where the app says it listens, at the profile's launchUrl.
        if (profile is { LaunchBrowser: true } && configuration["serverReadyAction"] is null)
            configuration["serverReadyAction"] = new JsonObject
            {
                ["action"] = "openExternally",
                ["pattern"] = @"\bNow listening on:\s+(https?://\S+)",
                ["uriFormat"] = "%s/" + (profile.LaunchUrl ?? ""),
            };
        log($"{project.Name}: {Path.GetFileName(program)}" + (profile is null ? "" : $", launch profile '{profile.Name}'"));
    }
}
