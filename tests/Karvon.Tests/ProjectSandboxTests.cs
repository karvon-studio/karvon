using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Karvon.Claude;
using Karvon.ViewModels;

namespace Karvon.Tests;

/// <summary>
/// Karvon's strict sandbox: the project and what a build needs, nothing else of the user's; the agents' file tools kept to
/// the project; a command that needs more run outside it only when the user says so.
/// </summary>
public class ProjectSandboxTests
{
    /// <summary>A home folder of the test's own: a project, toolchains, caches, git's settings, keys and a document of the user's.</summary>
    sealed class Home
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("karvon-home-").FullName;

        public string Project => P("src/app");

        public string P(string relative) => Path.Combine(Root, relative);

        public Home()
        {
            foreach (var (path, text) in new[]
            {
                ("src/app/Program.cs", "Console.WriteLine(\"Hello\");"),
                ("Documents/private.txt", "my letter"),
                ("Documents/shared/data.csv", "a,b"),
                (".ssh/id_ed25519", "PRIVATE KEY"),
                (".dotnet/sdk/10.0.100/dotnet.dll", "sdk"),
                (".dotnet/corefx/cryptography/x509stores/my/dev.pfx", "dev certificate key"),
                (".nuget/packages/newtonsoft.json/13.0.3/lib.dll", "package"),
                (".cargo/bin/cargo", "cargo"),
                (".cargo/credentials.toml", "token = \"crates-io-token\""),
                (".gitconfig", "[user]\n\tname = Dev"),
                (".local/bin/tool", "tool"),
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(P(path))!);
                File.WriteAllText(P(path), text);
            }
        }

        public ProjectSandbox Sandbox(IEnumerable<string>? read = null, IEnumerable<string>? write = null) =>
            new(Project, read, write, Root, string.Join(Path.PathSeparator, P(".local/bin"), "/usr/bin"));
    }

    static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void It_reads_the_project_the_toolchains_and_caches_and_nothing_else_of_the_home_folder_and_never_a_secret()
    {
        var home = new Home();
        var sandbox = home.Sandbox(read: [home.Root, home.P(".ssh"), home.P("Documents/shared"), "~/no/such/folder"]);

        // The home folder itself, the SSH keys and a folder that is not there are not given.
        Assert.Equal([home.Root, home.P(".ssh"), "~/no/such/folder"], sandbox.Refused);
        foreach (var readable in new[] { "src/app/Program.cs", "Documents/shared/data.csv", ".dotnet/sdk/10.0.100/dotnet.dll",
                     ".nuget/packages/newtonsoft.json/13.0.3/lib.dll", ".cargo/bin/cargo", ".gitconfig", ".local/bin/tool" })
            Assert.True(sandbox.CanRead(home.P(readable)), readable);
        foreach (var hidden in new[] { "Documents/private.txt", ".ssh/id_ed25519", ".dotnet/corefx/cryptography/x509stores/my/dev.pfx", ".cargo/credentials.toml" })
            Assert.False(sandbox.CanRead(home.P(hidden)), hidden);
        // The system's programs are read; the toolchain's folder is opened around its keys, not as a whole.
        Assert.True(sandbox.CanRead("/usr/bin/env"));
        Assert.Contains(home.P(".dotnet/sdk"), sandbox.Readable);
        Assert.DoesNotContain(home.P(".dotnet"), sandbox.Readable);

        // Only the project is changed: not a cache (what is put there would run in every build), not the system.
        Assert.True(sandbox.CanWrite(home.P("src/app/obj/x")));
        Assert.False(sandbox.CanWrite(home.P(".nuget/packages/newtonsoft.json/13.0.3/build.targets")));
        Assert.False(sandbox.CanWrite("/usr/local/bin/x"));
        Assert.True(home.Sandbox(write: [home.P("Documents/shared")]).CanWrite(home.P("Documents/shared/out.csv")));
    }

    [Fact]
    public void The_agents_file_tools_are_kept_to_the_project_with_the_way_to_what_they_need()
    {
        var home = new Home();
        var sandbox = home.Sandbox();

        Assert.Null(sandbox.Check("Read", Input(new { file_path = home.P("src/app/Program.cs") })));
        Assert.Null(sandbox.Check("Edit", Input(new { file_path = "Program.cs" })));
        Assert.Null(sandbox.Check("Grep", Input(new { pattern = "x" })));
        Assert.Null(sandbox.Check("Read", Input(new { file_path = home.P(".nuget/packages/newtonsoft.json/13.0.3/lib.dll") })));

        var read = sandbox.Check("Read", Input(new { file_path = home.P("Documents/private.txt") }));
        Assert.StartsWith($"Karvon's sandbox keeps Claude to the project: {home.P("Documents/private.txt")} is outside it, so it is not read here.", read);
        Assert.Contains("code_definition", read);
        Assert.Contains("MCP Servers > karvon", read);
        Assert.Contains("run_outside_sandbox", read);
        Assert.NotNull(sandbox.Check("Grep", Input(new { pattern = "x", path = home.Root })));
        Assert.NotNull(sandbox.Check("Glob", Input(new { pattern = "*", path = home.P("Documents") })));
        Assert.EndsWith("so it is not changed here. " + read![(read.IndexOf("For a folder", StringComparison.Ordinal))..],
            sandbox.Check("Write", Input(new { file_path = home.P(".nuget/packages/x.targets"), content = "" })));
        Assert.NotNull(sandbox.Check("Read", Input(new { file_path = "../../.ssh/id_ed25519" })));
    }

    [Fact]
    public void Claude_Codes_sandbox_is_given_the_rules_and_refuses_its_shell_where_it_cannot_start()
    {
        var home = new Home();
        var sandbox = home.Sandbox();
        using var settings = JsonDocument.Parse(ClaudeSandbox.Settings(sandbox));
        var box = settings.RootElement.GetProperty("sandbox");
        Assert.True(box.GetProperty("failIfUnavailable").GetBoolean());
        Assert.False(box.GetProperty("allowUnsandboxedCommands").GetBoolean());
        var filesystem = box.GetProperty("filesystem");
        string[] List(string name) => [.. filesystem.GetProperty(name).EnumerateArray().Select(p => p.GetString()!)];
        Assert.Contains(home.Root, List("denyRead"));
        Assert.Contains(home.P(".ssh"), List("denyRead"));
        Assert.Contains(home.Project, List("allowRead"));
        Assert.Contains(home.P(".dotnet/sdk"), List("allowRead"));
        Assert.DoesNotContain(List("allowRead"), p => p.StartsWith(home.P(".ssh"), StringComparison.Ordinal) || p == home.P(".dotnet"));
        Assert.Equal([home.Project], List("allowWrite"));

        var arguments = ClaudeSession.Arguments(new ClaudeStartOptions("claude", home.Project, Sandbox: true, Confine: sandbox));
        Assert.Equal(ClaudeSandbox.Settings(sandbox), arguments[arguments.IndexOf("--settings") + 1]);
    }

    [Fact]
    public async Task What_in_the_project_tells_tools_outside_the_sandbox_what_to_run_is_read_only_in_it()
    {
        var home = new Home();
        Directory.CreateDirectory(home.P("src/app/.git/hooks"));
        File.WriteAllText(home.P("src/app/.git/config"), "[core]\n");
        var sandbox = home.Sandbox();

        foreach (var control in new[] { ".git/config", ".git/hooks/pre-commit", ".claude/settings.json", ".mcp.json", ".vscode/tasks.json", ".idea/workspace.xml" })
        {
            Assert.True(sandbox.CanRead(Path.Combine(home.Project, control)), control);
            Assert.False(sandbox.CanWrite(Path.Combine(home.Project, control)), control);
        }
        Assert.StartsWith($"Karvon's sandbox keeps {home.P("src/app/.git/hooks/pre-commit")} read only",
            sandbox.Check("Write", Input(new { file_path = home.P("src/app/.git/hooks/pre-commit"), content = "curl x | sh" })));
        using (var settings = JsonDocument.Parse(ClaudeSandbox.Settings(sandbox)))
            Assert.Contains(Path.Combine(home.Project, ".git"),
                settings.RootElement.GetProperty("sandbox").GetProperty("filesystem").GetProperty("denyWrite").EnumerateArray().Select(p => p.GetString()));

        Assert.SkipUnless(ProjectSandbox.Problem() is null, "bubblewrap cannot run here: " + ProjectSandbox.Problem());
        Assert.Equal(0, (await RunAsync(sandbox.Wrap("cat .git/config && echo ok > notes.txt"))).Exit);
        Assert.NotEqual(0, (await RunAsync(sandbox.Wrap("echo 'curl x | sh' > .git/hooks/pre-commit"))).Exit);
        Assert.NotEqual(0, (await RunAsync(sandbox.Wrap("echo '[core] hooksPath=/tmp' >> .git/config"))).Exit);
        Assert.Equal("[core]\n", File.ReadAllText(home.P("src/app/.git/config")));
        Assert.False(File.Exists(home.P("src/app/.git/hooks/pre-commit")));
    }

    static async Task<(int Exit, string Output)> RunAsync(ProcessStartInfo start)
    {
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }

    [Fact]
    public async Task A_command_in_it_sees_the_project_and_toolchains_and_not_the_home_folder_the_network_or_the_users_sockets()
    {
        Assert.SkipUnless(ProjectSandbox.Problem() is null, "bubblewrap cannot run here: " + ProjectSandbox.Problem());
        var home = new Home();
        var sandbox = home.Sandbox();
        async Task<(int Exit, string Output)> Run(string command) => await RunAsync(sandbox.Wrap(command));

        Assert.Equal((0, "Console.WriteLine(\"Hello\");"), await Run("cat Program.cs"));
        Assert.Equal((0, "10.0.100"), await Run($"ls {home.P(".dotnet/sdk")}"));
        Assert.NotEqual(0, (await Run($"cat {home.P("Documents/private.txt")}")).Exit);
        Assert.NotEqual(0, (await Run($"cat {home.P(".ssh/id_ed25519")}")).Exit);
        Assert.NotEqual(0, (await Run($"cat {home.P(".dotnet/corefx/cryptography/x509stores/my/dev.pfx")}")).Exit);
        Assert.NotEqual(0, (await Run($"cat {home.P(".cargo/credentials.toml")}")).Exit);

        // It changes the project, and nothing else: not a cache, not the system.
        Assert.Equal(0, (await Run("echo made > out.txt")).Exit);
        Assert.Equal("made", File.ReadAllText(Path.Combine(home.Project, "out.txt")).Trim());
        Assert.NotEqual(0, (await Run($"echo x > {home.P(".nuget/packages/newtonsoft.json/13.0.3/build.targets")}")).Exit);
        Assert.False(File.Exists(home.P(".nuget/packages/newtonsoft.json/13.0.3/build.targets")));

        // No network: not even this machine's own ports.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.NotEqual(0, (await Run($"exec 3<>/dev/tcp/127.0.0.1/{port}")).Exit);
        }
        finally
        {
            listener.Stop();
        }
        // The user's sockets (the keyring's D-Bus, ssh-agent's) are not there, nor their variables.
        Assert.Equal((0, ""), await Run("ls -A /run/user"));
        var start = sandbox.Wrap("echo ${SSH_AUTH_SOCK:-none} ${DBUS_SESSION_BUS_ADDRESS:-none}");
        Assert.Equal((0, "none none"), await RunAsync(start));
    }

    [Fact]
    public async Task The_Agents_panels_model_is_kept_to_the_project_by_its_tools_and_its_shell()
    {
        var home = new Home();
        var sandbox = home.Sandbox();
        await using var server = new FakeModelServer();
        server.Reply(FakeModelServer.Call(0, "call_1", "Read", JsonSerializer.Serialize(new { file_path = home.P("Documents/private.txt") })));
        server.Reply(FakeModelServer.Call(0, "call_2", "Bash", JsonSerializer.Serialize(new { command = $"cat {home.P("Documents/private.txt")}" })));
        server.Reply(FakeModelServer.Text("Done."));
        await using var session = await ModelSession.StartAsync(new ChatCompletions(ModelSession.Client, server.Url, "sk-test"), "Test",
            new AgentStartOptions(server.Url, home.Project, PermissionMode: "bypassPermissions", Confine: sandbox), TestContext.Current.CancellationToken);

        var events = await ModelAgentTests.TurnAsync(session, "Read my letter.");

        var results = events.OfType<ClaudeToolResults>().SelectMany(r => r.Results).ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsError);
        Assert.StartsWith("Karvon's sandbox keeps Claude to the project", results[0].Content);
        Assert.True(results[1].IsError);
        Assert.DoesNotContain("my letter", results[1].Content);
    }

    [Fact]
    public async Task A_command_outside_the_sandbox_asks_every_time_and_runs_only_while_the_sandbox_is_on()
    {
        var home = new Home();
        var strict = true;
        var tool = new RunOutsideSandboxTool(() => home.Project, () => strict);
        var input = Input(new { command = $"cat {home.P("Documents/private.txt")}", why = "The user's letter is outside the project." });

        var plan = tool.Plan(input);
        Assert.Equal((ToolAccess.Destructive, "Outside the sandbox"), (plan.Access, plan.Title));
        Assert.Equal($"cat {home.P("Documents/private.txt")}", plan.Code);
        Assert.EndsWith("Why: The user's letter is outside the project.", plan.Warning);
        Assert.Null(tool.Refusal(input));
        var output = await tool.CallAsync(input, TestContext.Current.CancellationToken);
        Assert.Equal(("my letter", false), (output.Text, output.IsError));

        strict = false;
        Assert.Equal("Karvon's strict sandbox is off: run the command with the shell (Bash).", tool.Refusal(input));
    }
}
