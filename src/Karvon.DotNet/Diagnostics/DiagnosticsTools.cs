using System.Diagnostics;
using Karvon.Diagnostics;

namespace Karvon.DotNet.Diagnostics;

/// <summary>
/// Microsoft's diagnostics tools (dotnet-gcdump, dotnet-trace; MIT, on nuget.org as dotnet tools): found in
/// ~/.local/share/karvon/dotnet-diagnostics/&lt;version&gt;, installed there with dotnet tool install once the user says so.
/// KARVON_DOTNET_DIAGNOSTICS names a folder of them instead (off: none, as the tests have it).
/// </summary>
static class DiagnosticsTools
{
    public const string Version = "10.0.750501";

    public static string InstallRoot => Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "dotnet-diagnostics", Version);

    static string? Given => Environment.GetEnvironmentVariable("KARVON_DOTNET_DIAGNOSTICS") is { Length: > 0 } given ? given : null;

    public static string? Find(string tool)
    {
        if (Given == "off")
            return null;
        // On Windows a tool is an .exe (dotnet tool install's), or a script of the folder given.
        var dir = Given ?? InstallRoot;
        return (OperatingSystem.IsWindows() ? [tool + ".exe", tool + ".cmd"] : new[] { tool }).Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
    }

    public static async Task<string> EnsureAsync(string tool, DiagnosticRun run, CancellationToken ct)
    {
        if (Find(tool) is { } found)
            return found;
        if (Given is { } given)
            throw new InvalidOperationException(given == "off" ? $"{tool} is turned off (KARVON_DOTNET_DIAGNOSTICS)." : $"{tool} is not in {given}.");
        if (!await run.Confirm($"Install {tool}", $"{tool} {Version} (Microsoft's, from nuget.org) is needed. It is installed with dotnet tool install into {InstallRoot}. Install it now?"))
            throw new OperationCanceledException();
        // dotnet tool install writes into the folder as it goes: one at a time, the next finding it there.
        await using var held = await Karvon.Modules.InstallLock.AcquireAsync(InstallRoot, ct);
        if (Find(tool) is { } installed)
            return installed;
        run.Log($"Installing {tool} {Version}...");
        Directory.CreateDirectory(InstallRoot);
        // Outside any project, so a repo's NuGet.Config cannot redirect the package source.
        var (code, output) = await RunAsync("dotnet", ["tool", "install", tool, "--version", Version, "--tool-path", InstallRoot], InstallRoot, ct);
        if (code != 0)
            throw new InvalidOperationException($"dotnet tool install {tool} failed ({code}): {LastLine(output)}");
        return Find(tool) ?? throw new InvalidOperationException($"{tool} was installed but is not in {InstallRoot}.");
    }

    /// <summary>A tool run to its end (killed when canceled): its exit code, and what it wrote (both streams).</summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args, string directory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // dotnet-trace ends early on a key: it gets none.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    public static string LastLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "it said nothing";

    /// <summary>
    /// Whether a .NET runtime listens for diagnostics in the process (its dotnet-diagnostic socket, its pipe on Windows):
    /// not the SDK's own (dotnet run, MSBuild's nodes, the compiler server), which the program's run starts too.
    /// </summary>
    public static bool IsDotNetProgram(DiagnosedProcess process) => !IsSdk(process.CommandLine) && HasDiagnosticsPort(process.Pid);

    internal static bool IsSdk(string commandLine)
    {
        var words = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.EndsWith("MSBuild.dll", StringComparison.OrdinalIgnoreCase) || w.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase)
                || w.Contains("dotnet-watch", StringComparison.OrdinalIgnoreCase) || w.Contains("/sdk/", StringComparison.Ordinal) || w.Contains("\\sdk\\", StringComparison.Ordinal)))
            return true;
        return words is [var host, var verb, ..] && Path.GetFileNameWithoutExtension(host) == "dotnet"
            && verb is "run" or "watch" or "build" or "test" or "msbuild" or "build-server" or "tool";
    }

    static bool HasDiagnosticsPort(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Directory.EnumerateFiles(@"\\.\pipe\", $"dotnet-diagnostic-{pid}*").Any();
            return new[] { Path.GetTempPath(), "/tmp/" }.Distinct().Any(dir => Directory.Exists(dir) && Directory.EnumerateFiles(dir, $"dotnet-diagnostic-{pid}-*-socket").Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Delete(params string[] files)
    {
        foreach (var file in files)
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
    }
}
