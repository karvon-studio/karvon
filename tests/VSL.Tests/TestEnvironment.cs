using System.Runtime.CompilerServices;

namespace VSL.Tests;

/// <summary>
/// Before any test runs: VSL's Docker Explorer and Claude's Docker tools find the fake engine, the Servers and the MCP
/// Servers read homes of the tests' own (never the user's ~/.ssh or ~/.claude.json), no secret goes to the user's
/// keyring, and packages come from nuget.org only.
/// </summary>
static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        if (Environment.GetEnvironmentVariable("VSL_DOCKER_PATH") is null)
            Environment.SetEnvironmentVariable("VSL_DOCKER_PATH", FakeDocker.Create().Docker);
        if (Environment.GetEnvironmentVariable("VSL_SSH_HOME") is null)
        {
            var home = Path.Combine(Path.GetTempPath(), "vsl-ssh-home-" + Environment.ProcessId);
            Directory.CreateDirectory(Path.Combine(home, ".ssh"));
            Environment.SetEnvironmentVariable("VSL_SSH_HOME", home);
        }
        // The agents' MCP settings (~/.claude.json, ~/.codex, ~/.gemini) are read from a home of the tests', never the user's.
        if (Environment.GetEnvironmentVariable("VSL_USER_HOME") is null)
        {
            var home = Path.Combine(Path.GetTempPath(), "vsl-user-home-" + Environment.ProcessId);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("VSL_USER_HOME", home);
        }
        // Every restore and build the tests start (MSBuild reads its properties from the environment) uses nuget.org only:
        // a private feed of the machine's that cannot be reached would hold each restore until it gives up.
        if (Environment.GetEnvironmentVariable("RestoreConfigFile") is null)
            Environment.SetEnvironmentVariable("RestoreConfigFile", Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample", "nuget.config"));
        Environment.SetEnvironmentVariable("BENGINE_KEYRING", "off");
    }
}
