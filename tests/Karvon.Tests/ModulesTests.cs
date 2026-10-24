using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Mcp;
using Karvon.Mcp.Views;
using Karvon.Modules;
using Karvon.Skills;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Modules: what they add to the window comes from them, and one that is off adds nothing.</summary>
public class ModulesTests
{
    static TabItem? Tab(Window window, string id) => window.GetVisualDescendants().OfType<TabItem>().SingleOrDefault(t => (string?)t.Tag == id);

    static IEnumerable<string> ViewMenu(Window window) =>
        window.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_View")
            .Items.OfType<MenuItem>().Select(m => m.Header as string).OfType<string>();

    [AvaloniaFact]
    public async Task Modules_bring_their_tool_windows_documents_and_dialogs()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();

        // On by default, started with Karvon as their workbench; nothing of theirs is made before it shows.
        Assert.Equal(["database", "docker", "servers", "jira", "ports", "pipelines", "mcp", "skills", "dotnet", "typescript", "python", "go", "rust", "cpp", "java", "kotlin", "claude"], vm.Modules.Select(m => m.Id));
        var mcp = vm.Module<McpModule>()!;
        Assert.True(mcp.IsStarted);
        Assert.Null(mcp.Explorer);
        Assert.Null(mcp.Overlay);
        Assert.NotNull(Tab(window, McpModule.ToolWindowId));
        Assert.NotNull(Tab(window, SkillsModule.ToolWindowId));
        Assert.Equal("ToolMcp", ToolWindowTabs.IconOf(McpModule.ToolWindowId));
        var menu = ViewMenu(window).ToList();
        Assert.True(menu.IndexOf("_MCP Servers") < menu.IndexOf("S_kills"));

        // Shown: its view and explorer are made, and its dialogs can show over the window.
        vm.ShowToolWindow(McpModule.ToolWindowId);
        await WaitUntil(() => mcp.Explorer is not null, TimeSpan.FromSeconds(5));
        Assert.True(window.IsToolWindowShowing(McpModule.ToolWindowId));
        Assert.IsType<McpExplorerView>((Tab(window, McpModule.ToolWindowId)!.Content as ContentControl)?.Content);
        Assert.Same(mcp.Explorer, mcp.Overlay);
        Assert.Contains(window.GetVisualDescendants().OfType<McpOverlay>(), o => o.DataContext == mcp.Explorer);

        // Its documents look as the module says, in the document well's frame.
        mcp.Explorer!.OpenBuiltIn();
        await WaitUntil(() => window.GetVisualDescendants().OfType<McpBuiltInView>().Any(), TimeSpan.FromSeconds(5));
        Assert.Contains(window.GetVisualDescendants().OfType<McpBuiltInView>(), v => v.FindAncestorOfType<Border>() is { } frame && frame.Classes.Contains("docframe"));

        // A folder opened or another account: the module reads again (the workbench tells it).
        Assert.NotNull(((IWorkbench)vm).Service<IClaudeService>());
        Assert.Same(mcp, ((IWorkbench)vm).Service<McpModule>());
        window.Close();
    }

    [AvaloniaFact]
    public void A_module_turned_off_adds_nothing()
    {
        var vm = NewViewModel(settings =>
        {
            // Not written: the other tests share the settings file.
            settings.InMemory = true;
            settings.DisabledModules = ["mcp", "servers"];
        });
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        Assert.Equal(["database", "docker", "servers", "jira", "ports", "pipelines", "mcp", "skills", "dotnet", "typescript", "python", "go", "rust", "cpp", "java", "kotlin", "claude"], vm.AllModules.Select(m => m.Id));
        Assert.Equal(["database", "docker", "jira", "ports", "pipelines", "skills", "dotnet", "typescript", "python", "go", "rust", "cpp", "java", "kotlin", "claude"], vm.Modules.Select(m => m.Id));
        Assert.Null(vm.Module<McpModule>());
        Assert.False(vm.AllModules.OfType<McpModule>().Single().IsStarted);
        Assert.Null(Tab(window, McpModule.ToolWindowId));
        Assert.NotNull(Tab(window, SkillsModule.ToolWindowId));
        Assert.DoesNotContain("_MCP Servers", ViewMenu(window));
        Assert.Contains("S_kills", ViewMenu(window));
        // Without MCP Servers the Claude Code sessions get no other servers.
        Assert.Null(ClaudeOf(vm).ExtraMcpServers);
        // Each module's tools come with it: the Docker and server tools, not those of a module that is off.
        Assert.Contains(ClaudeOf(vm).ToolServer().Tools, t => t.Name == "container_logs");
        Assert.DoesNotContain(ClaudeOf(vm).ToolServer().Tools, t => t.Name == "server_list");
        Assert.Null(Tab(window, Karvon.Servers.ServersModule.ToolWindowId));
        window.Close();
    }
}
