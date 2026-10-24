using System.Runtime.CompilerServices;

namespace Karvon.Tests;

/// <summary>
/// Before any test runs: Karvon's settings are the tests' own (never the user's ~/.config/karvon), Karvon's Docker
/// Explorer and Claude's Docker tools find the fake engine, the Servers and the MCP Servers read homes of the tests' own
/// (never the user's ~/.ssh or ~/.claude.json), no secret goes to the user's keyring, and packages come from nuget.org only.
/// </summary>
static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        // macOS's temporary folder is /var/folders/..., /var a link to /private/var, which git, MSBuild and the
        // debugger answer with: the tests' folders are where those say they are.
        if (OperatingSystem.IsMacOS() && Path.GetTempPath() is var temp && temp.StartsWith("/var/", StringComparison.Ordinal) && Directory.Exists("/private" + temp))
            Environment.SetEnvironmentVariable("TMPDIR", "/private" + temp);
        // The modules the application comes with, as the application gives them.
        Karvon.Modules.ModuleCatalog.Create = Karvon.BuiltInModules.Create;
        // Never the user's real ~/.config/karvon, whichever test runs first (opening a folder saves the recent list).
        if (Environment.GetEnvironmentVariable("KARVON_CONFIG_DIR") is null)
            Environment.SetEnvironmentVariable("KARVON_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "karvon-test-config"));
        if (Environment.GetEnvironmentVariable("KARVON_DOCKER_PATH") is null)
            Environment.SetEnvironmentVariable("KARVON_DOCKER_PATH", FakeDocker.Create().Docker);
        if (Environment.GetEnvironmentVariable("KARVON_SSH_HOME") is null)
        {
            var home = Path.Combine(Path.GetTempPath(), "karvon-ssh-home-" + Environment.ProcessId);
            Directory.CreateDirectory(Path.Combine(home, ".ssh"));
            Environment.SetEnvironmentVariable("KARVON_SSH_HOME", home);
        }
        // The agents' MCP settings (~/.claude.json, ~/.codex, ~/.gemini) are read from a home of the tests', never the user's.
        if (Environment.GetEnvironmentVariable("KARVON_USER_HOME") is null)
        {
            var home = Path.Combine(Path.GetTempPath(), "karvon-user-home-" + Environment.ProcessId);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("KARVON_USER_HOME", home);
        }
        // Every restore and build the tests start (MSBuild reads its properties from the environment) uses nuget.org only:
        // a private feed of the machine's that cannot be reached would hold each restore until it gives up.
        if (Environment.GetEnvironmentVariable("RestoreConfigFile") is null)
            Environment.SetEnvironmentVariable("RestoreConfigFile", Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample", "nuget.config"));
        Environment.SetEnvironmentVariable("BENGINE_KEYRING", "off");
        // The Pipelines never read GitHub through the user's own gh.
        Environment.SetEnvironmentVariable("KARVON_GH_PATH", "off");
        // Karvon Studio's releases are never read from GitHub: a feed of the tests' own, with none.
        if (Environment.GetEnvironmentVariable("KARVON_UPDATE_FEED") is null)
        {
            var feed = Path.Combine(Path.GetTempPath(), "karvon-test-releases-" + Environment.ProcessId + ".json");
            File.WriteAllText(feed, "[]");
            Environment.SetEnvironmentVariable("KARVON_UPDATE_FEED", feed);
        }
        // The tests' repositories keep their files as written: Git for Windows' own configuration turns their lines to CRLF.
        if (Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT") is null)
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.autocrlf");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "false");
        }
    }
}
