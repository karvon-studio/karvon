using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The user's rules for Karvon's tools, enforced by its tool server whatever Claude Code's permission mode.</summary>
public class ClaudeToolPolicyTests
{
    sealed class Tool(string name, ToolAccess access, string? target = null) : IClaudeTool
    {
        int _calls;

        public int Calls => _calls;

        public string Name => name;

        public string Description => "A tool.";

        public JsonObject InputSchema => new() { ["type"] = "object" };

        public ClaudeToolPlan Plan(JsonElement input) => new(access, name, null, access == ToolAccess.Read ? "" : $"Allow Claude to {name}?");

        public string? Target(JsonElement input) => target;

        public Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ClaudeToolOutput("done"));
        }
    }

    static ToolRule Decide(ToolAccess access, string? target, Dictionary<string, string> rules)
    {
        var tool = new Tool("exec", access, target);
        return ClaudeToolPolicy.Decide(tool, tool.Plan(default), target, rules);
    }

    [Fact]
    public void A_rule_allows_asks_or_denies_and_a_destructive_call_always_asks()
    {
        // Without a rule, as the call's access says.
        Assert.Equal(ToolRule.Allow, Decide(ToolAccess.Read, null, []));
        Assert.Equal(ToolRule.Allow, Decide(ToolAccess.Run, null, []));
        Assert.Equal(ToolRule.Ask, Decide(ToolAccess.Write, null, []));
        Assert.Equal(ToolRule.Ask, Decide(ToolAccess.Destructive, null, []));
        // A rule lowers or raises it; no rule lets a destructive call through; any denies.
        Assert.Equal(ToolRule.Allow, Decide(ToolAccess.Write, null, new() { ["exec"] = "allow" }));
        Assert.Equal(ToolRule.Ask, Decide(ToolAccess.Read, null, new() { ["exec"] = "Ask" }));
        Assert.Equal(ToolRule.Ask, Decide(ToolAccess.Destructive, null, new() { ["exec"] = "allow" }));
        Assert.Equal(ToolRule.Deny, Decide(ToolAccess.Destructive, null, new() { ["exec"] = "deny" }));
        Assert.Equal(ToolRule.Deny, Decide(ToolAccess.Read, null, new() { ["exec"] = "deny" }));
        Assert.Equal(ToolRule.Ask, Decide(ToolAccess.Write, null, new() { ["exec"] = "maybe" }));
        // A target's rule before the tool's, its name in any case.
        Dictionary<string, string> rules = new() { ["exec"] = "allow", ["exec@prod-1"] = "deny" };
        Assert.Equal(ToolRule.Allow, Decide(ToolAccess.Write, "dev", rules));
        Assert.Equal(ToolRule.Deny, Decide(ToolAccess.Write, "PROD-1", rules));
    }

    static async Task<(string Text, bool IsError)> Call(ClaudeToolServer server, string tool, object arguments)
    {
        var message = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        var result = (await server.HandleAsync(message, CancellationToken.None, caller: "session"))["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_tool_server_enforces_them_whatever_Claude_Code_let_through()
    {
        var read = new Tool("read", ToolAccess.Read);
        var write = new Tool("write", ToolAccess.Write, "shop");
        Dictionary<string, string> rules = [];
        var server = KarvonTools.Create([read, write]);
        server.Rules = () => rules;

        // A change Claude Code did not ask about, with no one to ask: it does not run.
        var (text, isError) = await Call(server, "write", new { a = 1 });
        Assert.True(isError);
        Assert.Equal("This call needs the user's yes, and Karvon could not ask them.", text);

        // Asked by Karvon: no, then yes.
        List<ClaudeToolRequest> asked = [];
        var answer = false;
        server.Ask = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(answer);
        };
        (text, isError) = await Call(server, "write", new { a = 1 });
        Assert.True(isError);
        Assert.StartsWith("The user said no to this call.", text);
        var request = Assert.Single(asked);
        Assert.Equal(("mcp__karvon__write", "Allow Claude to write?", "shop", (object?)"session"), (request.ToolName, request.Plan.Question, request.Target, request.Caller));
        answer = true;
        Assert.Equal(("done", false), await Call(server, "write", new { a = 1 }));
        Assert.Equal(1, write.Calls);

        // Allowed on Claude Code's prompt: that call runs once without being asked again (its JSON in any order).
        server.Approve("mcp__karvon__write", JsonSerializer.SerializeToElement(new { b = "é", a = 1 }));
        Assert.Equal(("done", false), await Call(server, "write", new { a = 1, b = "é" }));
        Assert.Equal(2, asked.Count);
        await Call(server, "write", new { a = 1, b = "é" });
        Assert.Equal(3, asked.Count);
        // Another call is asked about.
        server.Approve("mcp__karvon__write", JsonSerializer.SerializeToElement(new { a = 2 }));
        await Call(server, "write", new { a = 3 });
        Assert.Equal(4, asked.Count);

        // Denied: not run, not asked; a read asked about when the rules say so.
        rules["read"] = "deny";
        (text, isError) = await Call(server, "read", new { });
        Assert.True(isError);
        Assert.Equal("The user's rules for Karvon's tools deny read. Do not try it another way: tell the user what you wanted to do.", text);
        rules["write@shop"] = "deny";
        Assert.EndsWith("deny write on shop. Do not try it another way: tell the user what you wanted to do.", (await Call(server, "write", new { a = 1 })).Text);
        Assert.Equal((0, 4), (read.Calls, asked.Count));
        rules["read"] = "ask";
        Assert.Equal(("done", false), await Call(server, "read", new { }));
        Assert.Equal("Allow Claude to use read?", asked.Last().Plan.Question);

        // What Claude Code is answered when it asks: the rules' answer at once, or the user's.
        rules.Clear();
        rules["write"] = "allow";
        Assert.Equal(ToolRule.Allow, server.Decide("mcp__karvon__write", JsonSerializer.SerializeToElement(new { }))!.Rule);
        Assert.Equal(ToolRule.Allow, server.Decide("mcp__karvon__read", JsonSerializer.SerializeToElement(new { }))!.Rule);
        Assert.Null(server.Decide("Bash", JsonSerializer.SerializeToElement(new { })));
    }

    [AvaloniaFact]
    public async Task Bypass_permissions_does_not_skip_them()
    {
        var (dir, log) = FakeClaude.Workspace();
        var change = new Tool("change", ToolAccess.Write);
        var refused = new Tool("refused", ToolAccess.Read);
        var trusted = new Tool("trusted", ToolAccess.Write);
        var server = KarvonTools.Create([change, refused, trusted]);
        server.Rules = () => new Dictionary<string, string> { ["refused"] = "deny", ["trusted"] = "allow" };
        var vm = new ClaudeViewModel(new Karvon.Services.Settings { TrustedFolders = [dir] }, () => dir, () => null, () => [], () => FakeClaude.Executable) { Tools = server };
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        vm.Prompt = "karvonrules please";
        await vm.SendCommand.ExecuteAsync(null);

        // Called without Claude Code asking: Karvon asks on its own card, each time.
        var first = await WaitFor(vm, c => c.IsPending);
        Assert.Equal("Allow Claude to change?", first.Question);
        await vm.AllowCommand.ExecuteAsync(first);
        var second = await WaitFor(vm, c => c.IsPending && c != first);
        await vm.DenyCommand.ExecuteAsync(second);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));

        Assert.Equal(1, change.Calls);
        var rows = vm.Items.OfType<ToolChatItem>().ToDictionary(t => t.ToolUseId);
        Assert.True(rows["toolu_b1"].IsDone);
        Assert.True(rows["toolu_b2"].IsError);
        Assert.StartsWith("The user said no to this call.", rows["toolu_b2"].Result);
        // Asked as any tool: what the rules deny or allow is answered at once, with no card.
        Assert.Equal(2, vm.Items.OfType<PermissionChatItem>().Count());
        Assert.Equal((0, 1), (refused.Calls, trusted.Calls));
        var decisions = FakeClaude.Log(log).Where(e => e.Kind == "decision").Select(e => e.Data).ToList();
        Assert.Equal("deny", decisions[0].GetProperty("behavior").GetString());
        Assert.Contains("deny refused", decisions[0].GetProperty("message").GetString());
        Assert.Equal("allow", decisions[1].GetProperty("behavior").GetString());
        await vm.DisposeAsync();
    }

    static async Task<PermissionChatItem> WaitFor(ClaudeViewModel vm, Func<PermissionChatItem, bool> match)
    {
        PermissionChatItem? found = null;
        await WaitUntil(() => (found = vm.Items.OfType<PermissionChatItem>().FirstOrDefault(match)) is not null, TimeSpan.FromSeconds(10));
        return found!;
    }

    [Fact]
    public void Trusted_are_the_users_folders_and_what_is_in_them()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repo = Path.Combine(Path.GetTempPath(), "karvon-trust-" + Guid.NewGuid().ToString("N"));
        List<string> trusted = [];
        Assert.True(WorkspaceTrust.IsTrusted(trusted, home));
        Assert.False(WorkspaceTrust.IsTrusted(trusted, repo));
        WorkspaceTrust.Trust(trusted, repo + Path.DirectorySeparatorChar);
        Assert.Equal([repo], trusted);
        Assert.True(WorkspaceTrust.IsTrusted(trusted, Path.Combine(repo, "src")));
        Assert.False(WorkspaceTrust.IsTrusted(trusted, repo + "-fork"));
        WorkspaceTrust.Trust(trusted, Path.Combine(repo, "src"));
        Assert.Single(trusted);

        // Not trusted: only reading goes at once, whatever the rules allow; what they deny stays denied.
        var read = new Tool("read", ToolAccess.Read);
        var build = new Tool("run_build", ToolAccess.Run);
        var write = new Tool("write", ToolAccess.Write);
        Assert.Equal(ToolRule.Allow, ClaudeToolPolicy.Decide(read, read.Plan(default), null, null, trusted: false));
        Assert.Equal(ToolRule.Ask, ClaudeToolPolicy.Decide(build, build.Plan(default), null, null, trusted: false));
        Assert.Equal(ToolRule.Ask, ClaudeToolPolicy.Decide(write, write.Plan(default), null, new Dictionary<string, string> { ["write"] = "allow" }, trusted: false));
        Assert.Equal(ToolRule.Deny, ClaudeToolPolicy.Decide(read, read.Plan(default), null, new Dictionary<string, string> { ["read"] = "deny" }, trusted: false));

        // Claude Code reads none of the folder's settings (its hooks, allow rules, .mcp.json): only the user's.
        var args = ClaudeSession.Arguments(new ClaudeStartOptions("claude", repo, Trusted: false));
        Assert.Equal("user", args[args.IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain("--setting-sources", ClaudeSession.Arguments(new ClaudeStartOptions("claude", repo)));
    }

    [AvaloniaFact]
    public async Task A_folder_not_trusted_yet_says_so_until_the_user_trusts_it()
    {
        var (dir, log) = FakeClaude.Workspace();
        var settings = new Karvon.Services.Settings();
        var build = new Tool("run_build", ToolAccess.Run);
        var server = KarvonTools.Create([build]);
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable) { Tools = server };
        var window = new Avalonia.Controls.Window { Width = 560, Height = 700, Content = new Karvon.Views.ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // The banner, the session started without the folder's settings, and the build asked about.
        Assert.False(vm.IsFolderTrusted);
        var banner = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "TrustBanner");
        Assert.True(banner.IsVisible);
        var argv = FakeClaude.Log(log).First(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("user", argv[argv.IndexOf("--setting-sources") + 1]);
        Assert.Equal(ToolRule.Ask, server.Decide("mcp__karvon__run_build", JsonSerializer.SerializeToElement(new { }))!.Rule);
        Snapshot(window, "claude-untrusted-folder");

        // Trusted: kept in the settings, the build at once from now on, the banner gone.
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "TrustButton").Command!.Execute(null);
        Assert.Equal([dir], settings.TrustedFolders.Select(Path.TrimEndingDirectorySeparator));
        Assert.True(vm.IsFolderTrusted);
        Assert.Equal(ToolRule.Allow, server.Decide("mcp__karvon__run_build", JsonSerializer.SerializeToElement(new { }))!.Rule);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);
        await vm.DisposeAsync();
        window.Close();
    }
}
