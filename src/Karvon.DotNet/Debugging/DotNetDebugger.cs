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
    /// <param name="offerTrust">Asks whether to trust the development certificate, for a launch profile that listens on https.</param>
    public static DebuggerInfo Info(IWorkbench workbench, Func<LaunchProfile?, Task> offerTrust) =>
        new("netcoredbg", $"netcoredbg {NetcoredbgLocator.Version}", ["coreclr", "dotnet"], async start =>
        {
            var console = UsesConsole(start.Configuration, IsDotnet(start.Configuration) ? ProjectOf(start.Configuration, workbench) : null);
            if (IsDotnet(start.Configuration))
                await ResolveAsync(start.Configuration, workbench, start.Log);
            var adapter = Netcoredbg.Adapter(await NetcoredbgLocator.EnsureInstalledAsync(start.Log, start.Cancellation));
            if (console && IsLaunch(start.Configuration))
                await StartInConsoleAsync(start.Configuration, workbench, start.Log);
            return adapter;
        })
        {
            // Visual Studio asks on the first HTTPS run, with or without the debugger.
            PrepareAsync = async launch =>
            {
                if (IsDotnet(launch.Configuration) && ProjectOf(launch.Configuration, workbench) is { } project)
                    await offerTrust(ProfileOf(launch.Configuration, project));
                return true;
            },
            RunWithoutDebuggingAsync = configuration => Task.FromResult(IsDotnet(configuration) ? Run(configuration, workbench)
                : IsLaunch(configuration) && UsesConsole(configuration, null) ? ConsoleRunOf(configuration) : null),
        };

    static bool IsLaunch(JsonObject configuration) => (configuration["request"]?.GetValue<string>() ?? "launch") == "launch";

    /// <summary>A console app: an Exe of the plain .NET SDK (not a web app's, a worker's or a window's).</summary>
    internal static bool IsConsoleApp(ProjectInfo project) =>
        string.Equals(project.OutputType, "Exe", StringComparison.OrdinalIgnoreCase) && string.Equals(project.Sdk, "Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the program runs in a console of its own, as Visual Studio runs a console app in its console window: as the
    /// configuration's "console" says (VS Code's integratedTerminal and externalTerminal both are Karvon's console), else
    /// for a console app. Its output in Output instead, and no keyboard, with "internalConsole".
    /// </summary>
    internal static bool UsesConsole(JsonObject configuration, ProjectInfo? project) => configuration["console"]?.GetValue<string>() switch
    {
        "internalConsole" => false,
        "integratedTerminal" or "externalTerminal" => true,
        _ => project is not null && IsConsoleApp(project),
    };

    /// <summary>Ctrl+F5 of a "coreclr" launch in its console: its program as it is.</summary>
    static WorkspaceTask ConsoleRunOf(JsonObject configuration) =>
        new(Path.GetFileNameWithoutExtension(ConsoleArgs(configuration).FirstOrDefault() ?? configuration["program"]?.GetValue<string>() ?? "program"),
            WorkspaceTaskKind.Run, configuration["program"]?.GetValue<string>() ?? throw new InvalidOperationException("The configuration has no \"program\"."),
            ConsoleArgs(configuration), configuration["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory)
        {
            Environment = ConsoleEnvironment(configuration),
            RunsInConsole = true,
        };

    static IReadOnlyList<string> ConsoleArgs(JsonObject configuration) =>
        configuration["args"] is JsonArray args ? [.. args.Select(a => a?.GetValue<string>() ?? "")] : [];

    static Dictionary<string, string> ConsoleEnvironment(JsonObject configuration) =>
        configuration["env"] is JsonObject env
            ? env.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!.GetValue<string>(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The startup hook beside Karvon's assemblies (Karvon.DotNet.DebugWait): it holds the program before Main until the debugger is there.</summary>
    static string HookPath => Path.Combine(AppContext.BaseDirectory, "Karvon.DotNet.DebugWait.dll");

    /// <summary>
    /// F5 of a console app, in its console: netcoredbg runs a program with its output its own (its "console" aside), so
    /// the program starts in the console here, held before Main by a startup hook that says so in a file, and the launch
    /// becomes an attach to it (its breakpoints set before Main runs). Without the hook (its file gone), it stays a launch.
    /// </summary>
    static async Task StartInConsoleAsync(JsonObject configuration, IWorkbench workbench, Action<string> log)
    {
        if (!File.Exists(HookPath))
        {
            log($"{Path.GetFileName(HookPath)} is not beside Karvon: the program's output goes to Output.");
            return;
        }
        var marker = Path.Combine(Path.GetTempPath(), $"karvon-debug-{Guid.NewGuid():N}.pid");
        var environment = ConsoleEnvironment(configuration);
        environment["DOTNET_STARTUP_HOOKS"] = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") is { Length: > 0 } hooks
            ? hooks + Path.PathSeparator + HookPath
            : HookPath;
        environment["KARVON_DEBUGGER_WAIT"] = marker;
        var program = configuration["program"]?.GetValue<string>() ?? throw new InvalidOperationException("The configuration has no \"program\".");
        var args = ConsoleArgs(configuration);
        var name = Path.GetFileNameWithoutExtension(args.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? program);
        var console = await workbench.StartInConsoleAsync(new WorkspaceTask(name, WorkspaceTaskKind.Run, program, args,
            configuration["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory) { Environment = environment });
        var processId = console.ProcessId;
        try
        {
            // The hook's file once the runtime is up (the program is held there), with the program's process id (the
            // console's may be a shell's that became it); its exit, if it never gets there.
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(marker) && !console.Exited.IsCompleted && DateTime.UtcNow < until)
                await Task.Delay(25);
            if (File.Exists(marker) && int.TryParse((await File.ReadAllTextAsync(marker)).Trim(), out var id))
                processId = id;
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                File.Delete(marker);
            }
            catch (IOException)
            {
            }
        }
        log($"{name} runs in its console (the Terminal window); the debugger attaches to it.");
        configuration["request"] = "attach";
        configuration["processId"] = processId;
        foreach (var launchOnly in new[] { "program", "args", "cwd", "env", "console", "stopAtEntry" })
            configuration.Remove(launchOnly);
    }

    static bool IsDotnet(System.Text.Json.Nodes.JsonObject configuration) => configuration["type"]?.GetValue<string>() == "dotnet";

    /// <summary>The project a "dotnet" configuration names, or null when it is not there.</summary>
    static ProjectInfo? ProjectOf(JsonObject configuration, IWorkbench workbench)
    {
        if (configuration["projectPath"]?.GetValue<string>() is not { Length: > 0 } p)
            return null;
        string path;
        try
        {
            path = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return workbench.Solution?.Projects.FirstOrDefault(x => x.FilePath == path)
            ?? (File.Exists(path) ? ProjectReader.Load(Path.GetFileNameWithoutExtension(path), path) : null);
    }

    static LaunchProfile? ProfileOf(JsonObject configuration, ProjectInfo project) =>
        configuration["launchProfile"]?.GetValue<string>() is { } name ? project.LaunchProfiles.FirstOrDefault(x => x.Name == name) : null;

    /// <summary>
    /// Ctrl+F5 of a "dotnet" configuration, as Visual Studio runs a project without the debugger: dotnet run of what the
    /// build made, with its launch profile (its environment, URLs and arguments), the browser opening where it listens;
    /// with Hot Reload on, dotnet watch of it (see <see cref="DotNetWatch"/>), unless it runs in its console.
    /// </summary>
    static WorkspaceTask Run(JsonObject configuration, IWorkbench workbench)
    {
        var project = ProjectOf(configuration, workbench)
            ?? throw new InvalidOperationException($"{configuration["projectPath"]?.GetValue<string>() ?? "its projectPath"} was not found.");
        var profile = ProfileOf(configuration, project);
        var console = UsesConsole(configuration, project);
        var hotReload = workbench.Settings.HotReload && !console;
        var build = configuration["configuration"]?.GetValue<string>() ?? workbench.Configuration;
        List<string> args = hotReload ? DotNetWatch.Arguments(project.FilePath, build) : ["run", "--project", project.FilePath, "-c", build, "--no-build"];
        args.AddRange(profile is not null ? ["--launch-profile", profile.Name] : ["--no-launch-profile"]);
        return new WorkspaceTask(project.Name, WorkspaceTaskKind.Run, DotnetCli.Executable, args, project.Directory)
        {
            Environment = hotReload ? DotNetWatch.Environment : OutputEnvironment,
            ServerReadyAction = profile is { LaunchBrowser: true } ? BrowserAt(profile) : null,
            RunsInConsole = console,
            HotReloads = hotReload,
            OutputReader = hotReload ? DotNetWatch.Reader : null,
        };
    }

    /// <summary>Visual Studio's launchBrowser: the browser opens where the app says it listens, at the profile's launchUrl.</summary>
    internal static JsonObject BrowserAt(LaunchProfile profile) => new()
    {
        ["action"] = "openExternally",
        ["pattern"] = @"\bNow listening on:\s+(https?://\S+)",
        ["uriFormat"] = "%s/" + (profile.LaunchUrl ?? ""),
    };

    /// <summary>
    /// What a .NET program writes, as a terminal shows it: its colors kept when its output is redirected, and none of
    /// the Debug logger's lines (the Output window has them already).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> OutputEnvironment = new Dictionary<string, string>
    {
        ["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1",
        ["Logging__Debug__LogLevel__Default"] = "None",
    };

    /// <summary>A "dotnet" configuration ("projectPath", "launchProfile", "configuration", "args", "env") as the "coreclr" launch it is.</summary>
    internal static async Task ResolveAsync(JsonObject configuration, IWorkbench workbench, Action<string> log)
    {
        if (configuration["projectPath"]?.GetValue<string>() is not { Length: > 0 } path)
            throw new InvalidOperationException("A \"dotnet\" configuration names its project in \"projectPath\".");
        var project = ProjectOf(configuration, workbench) ?? throw new InvalidOperationException($"{path} was not found.");
        var buildConfiguration = configuration["configuration"]?.GetValue<string>() ?? workbench.Configuration;
        var program = await DotNetProjects.TargetPathAsync(project, buildConfiguration);
        if (program is null || !File.Exists(program))
            throw new InvalidOperationException($"the output assembly of {project.Name} was not found: build it first.");
        var profileName = configuration["launchProfile"]?.GetValue<string>();
        var profile = ProfileOf(configuration, project);
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
        if (profile is { LaunchBrowser: true } && configuration["serverReadyAction"] is null)
            configuration["serverReadyAction"] = BrowserAt(profile);
        log($"{project.Name}: {Path.GetFileName(program)}" + (profile is null ? "" : $", launch profile '{profile.Name}'"));
    }
}
