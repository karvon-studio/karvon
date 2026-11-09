using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

public class AgentsModuleTests
{
    [AvaloniaFact]
    public void Agents_is_a_module_of_its_own_and_Claude_Code_s_panel_keeps_to_Claude()
    {
        var vm = NewViewModel();

        var claude = ClaudeOf(vm).Claude;
        Assert.True(claude.IsClaude);
        Assert.Null(claude.Backend);
        Assert.Empty(claude.Backends);
        AgentPickerViewModel? asked = null;
        claude.PickAgentRequested += p => asked = p;
        claude.PickAgentCommand.Execute(null);
        Assert.Null(asked);

        var agents = vm.Module<AgentsModule>()!;
        Assert.Equal(("agents", "Agents"), (agents.Id, agents.Name));
        Assert.Equal(AgentsModule.ToolWindowId, Assert.Single(agents.ToolWindows).Id);
        Assert.False(agents.Agents.IsClaude);
        Assert.Equal(["codex", "openrouter", "ollama", "lmstudio", "acp:gemini"], agents.Agents.Backends.Select(b => b.Id));
        // The user's own ACP agents follow.
        var settings = new Karvon.Services.Settings { AcpAgents = [new() { Name = "My Agent", Command = "my-agent", Arguments = ["acp"] }] };
        Assert.Equal("acp:my-agent", AgentsModule.Backends(settings).Last().Id);
    }

    [AvaloniaFact]
    public async Task Choose_Agent_and_Model_lists_each_agent_with_whether_it_is_there_and_switches_the_panel()
    {
        var (dir, log) = FakeAcp.Workspace();
        _ = FakeCodex.Executable;
        var missing = new AcpBackend(new AcpAgent("missing", "Missing Agent", "Not here", [Path.Combine(dir, "no-such-agent")], [], [], "npm install -g missing"));
        var openRouter = new ModelBackend(ModelProvider.OpenRouter with { EnvironmentKey = "KARVON_TEST_NO_SUCH_KEY" });
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => null)
        {
            Backends = [new CodexBackend(), openRouter, new AcpBackend(FakeAcp.Agent), missing],
        };
        Assert.Equal("Codex", vm.AgentName);

        var picker = vm.CreatePicker();
        Assert.Equal(["Ready", "Needs an API key", "Ready", "Not installed"], picker.Providers.Select(p => p.Status));
        Assert.Equal("Codex", picker.SelectedProvider!.Name);
        await picker.Loaded;
        Assert.Equal(["gpt-fake"], picker.Models.Select(m => m.Id));

        // Not installed: it cannot be used.
        picker.SelectedProvider = picker.Providers[3];
        Assert.True(picker.SelectedProvider.IsMissing);
        Assert.False(picker.UseCommand.CanExecute(null));

        picker.SelectedProvider = picker.Providers[2];
        await picker.Loaded;
        Assert.Equal(["fake-1", "fake-2"], picker.Models.Select(m => m.Id));
        picker.Search = "two";
        picker.SelectedModel = Assert.Single(picker.Models);
        picker.UseCommand.Execute(null);
        var (backend, model) = picker.Chosen!.Value;
        await vm.UseAgentAsync(backend, model);

        Assert.Equal("Fake Agent", vm.AgentName);
        Assert.Equal(("acp:fake", "fake-2"), (settings.AgentBackend, settings.AgentModels["acp:fake"]));
        await WaitUntil(() => FakeAcp.Sent(log, "session/set_model").Any(p => p.GetProperty("modelId").GetString() == "fake-2"), TimeSpan.FromSeconds(15));
        Assert.Equal("fake-2", vm.SelectedModel!.Value);

        // Again with the conversation open: its models come from it, not from another session.
        var again = vm.CreatePicker();
        await again.Loaded;
        Assert.Equal("Fake Agent", again.SelectedProvider!.Name);
        Assert.Equal("fake-2", again.SelectedModel?.Id);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Equal(1 + 1, FakeAcp.Sent(log, "session/new").Count);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task The_dialog_shows_the_agents_and_closes_on_cancel_with_nothing_chosen()
    {
        var (dir, _) = FakeAcp.Workspace();
        var picker = new AgentPickerViewModel([new AcpBackend(FakeAcp.Agent)], null, null, () => dir);
        var window = new AgentPickerWindow { DataContext = picker };
        window.Show();
        await picker.Loaded;
        await WaitUntil(() => window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ModelList").ItemCount == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(1, window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProviderList").ItemCount);

        picker.CancelCommand.Execute(null);
        Assert.False(window.IsVisible);
        Assert.Null(picker.Chosen);
    }
}
