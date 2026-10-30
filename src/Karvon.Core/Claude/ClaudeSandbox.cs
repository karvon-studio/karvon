using System.Text.Json;

namespace Karvon.Claude;

/// <summary>
/// Claude Code's own sandbox for its shell (bubblewrap on Linux, Seatbelt on macOS), as Karvon starts it when the user turns
/// it on: what holds secrets not readable (<see cref="SecretFiles.UserPlaces"/>), no way out of the sandbox for a command,
/// Bash asking as before, and the package registries and code hosts reached without a prompt (any other host asks). On
/// Linux no Unix socket is reached from it, the keyring's D-Bus and ssh-agent's among them: a push over SSH does not work
/// from it (it would have to run outside the sandbox, which these settings refuse), and is done from Git Changes.
/// </summary>
public static class ClaudeSandbox
{
    /// <summary>The hosts a build, a restore or a clone reaches.</summary>
    static readonly string[] Domains =
    [
        "nuget.org", "*.nuget.org", "builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "aka.ms",
        "registry.npmjs.org", "*.npmjs.org", "registry.yarnpkg.com", "pypi.org", "files.pythonhosted.org",
        "crates.io", "*.crates.io", "proxy.golang.org", "sum.golang.org", "repo.maven.apache.org", "repo1.maven.org",
        "plugins.gradle.org", "services.gradle.org", "github.com", "*.github.com", "*.githubusercontent.com", "gitlab.com",
    ];

    /// <summary>The settings Claude Code is given with --settings.</summary>
    public static string Settings() => JsonSerializer.Serialize(new
    {
        sandbox = new
        {
            enabled = true,
            // A machine whose sandbox cannot start runs Claude Code without it; Karvon says why (Problem).
            failIfUnavailable = false,
            allowUnsandboxedCommands = false,
            autoAllowBashIfSandboxed = false,
            filesystem = new { denyRead = SecretFiles.UserPlaces().ToArray() },
            network = new { allowedDomains = Domains },
        },
    });

    static readonly Lazy<string?> Checked = new(Check);

    /// <summary>Why the sandbox cannot start on this machine, with what to do; null when nothing stops it that Karvon knows of (asked once).</summary>
    public static string? Problem() => Checked.Value;

    static string? Check()
    {
        if (OperatingSystem.IsWindows())
            return null;
        if (OperatingSystem.IsMacOS())
            return File.Exists("/usr/bin/sandbox-exec") ? null : "macOS's sandbox-exec is not there.";
        if (Workspace.TaskVariables.Find("bwrap", Environment.GetEnvironmentVariable("PATH") ?? "", Environment.CurrentDirectory) is not { } bwrap)
            return "bubblewrap is not installed: sudo apt install bubblewrap (or your distribution's package).";
        // What the sandbox does first: a user namespace of its own, which AppArmor may refuse (Ubuntu's restriction).
        return Starts(bwrap, ["--ro-bind", "/", "/", "--unshare-user", "--unshare-pid", "true"]) ? null
            : "bubblewrap cannot make its user namespace here (AppArmor's restriction of unprivileged user namespaces, on Ubuntu): "
                + "add bubblewrap's AppArmor profile (https://code.claude.com/docs/en/sandboxing), "
                + "or run sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0 (until a restart).";
    }

    static bool Starts(string program, string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(program)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
