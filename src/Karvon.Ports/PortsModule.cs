using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Modules;
using Karvon.Ports.ViewModels;
using Karvon.Ports.Views;

namespace Karvon.Ports;

/// <summary>
/// Ports: port forwarding as Visual Studio Code's, through a jprq tunnel service (tulki.uz by default) in a tool window
/// (see <see cref="PortsViewModel"/>): a local port at a public address, the running program's ports offered, Claude Code's
/// tunnel tools. The service's API key stays in the keyring.
/// </summary>
public sealed partial class PortsModule : KarvonModule, IPortsHost
{
    public const string ToolWindowId = "Ports";

    const string ForwardsKey = "ports.forwards";

    public override string Id => "ports";

    public override string Name => "Ports";

    public override string Icon => "folder-global";

    public override string Description =>
        "Port forwarding: a local port at a public address (a web app at https://name.tulki.uz, SSH or a database at a host and "
        + "port) through a jprq tunnel service, the running program's ports offered; Claude Code's tunnel tools, which ask before "
        + "a port goes public (the service's API key stays in the system keyring, Claude never sees it).";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Ports", "_Ports", "ToolPorts", DockSide.Bottom, () => new PortsView { DataContext = EnsurePorts() })];

    public override IDataTemplate OverlayTemplate => Template<PortsViewModel>(_ => new PortsOverlay());

    /// <summary>The window's view model, once it has been needed.</summary>
    [ObservableProperty]
    public partial PortsViewModel? Ports { get; private set; }

    /// <summary>How the window resolves the service (the tests' fake server).</summary>
    internal Func<string, CancellationToken, Task<JprqEndpoint>>? Resolve { get; set; }

    internal ICredentialStore? Keyring { get; set; } = Karvon.Services.Keyring.System;

    public PortsViewModel EnsurePorts()
    {
        if (Ports is null)
        {
            Overlay = Ports = new PortsViewModel(this, Keyring, Resolve);
            _ = Ports.RestoreAsync(Saved());
        }
        return Ports;
    }

    IEnumerable<SavedForward> Saved()
    {
        try
        {
            return Workbench.WorkspaceState.GetValueOrDefault(ForwardsKey) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<SavedForward>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    protected override void Started() => Workbench.ListeningChanged += OnListeningChanged;

    void OnListeningChanged() => ListeningChanged?.Invoke();

    /// <summary>Another solution: its own forwarded ports, stopped (those of the last one stop).</summary>
    public override async Task WorkspaceChangedAsync()
    {
        if (Ports is not null)
            await Ports.RestoreAsync(Saved());
    }

    public override void SaveWorkspaceState(IDictionary<string, string> state)
    {
        if (Ports is { Forwards.Count: > 0 } ports)
            state[ForwardsKey] = JsonSerializer.Serialize(ports.Saved);
    }

    /// <summary>The solution closes: nothing of it stays public.</summary>
    public override async Task<bool> CloseWorkspaceAsync()
    {
        if (Ports is not null)
            await Ports.StopAllAsync();
        return true;
    }

    public override void Dispose()
    {
        Workbench.ListeningChanged -= OnListeningChanged;
        Ports?.Dispose();
        Overlay = Ports = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => PortsClaudeTools.Create(EnsurePorts);

    /// <summary>A tunnel by jprq's CLI, ngrok or cloudflared: Karvon's Ports instead (its key, its window).</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("tunnel cli", @"(jprq|ngrok)\s+(http|tcp)\b|cloudflared\s+tunnel\b", Use),
    ];

    const string Use =
        "Karvon forwards ports itself (its Ports window, the tunnel service's key in the system keyring): use "
        + "mcp__karvon__tunnel_open (the user approves it), tunnel_list and tunnel_close, not a tunnel CLI; no key is needed.";

    /// <summary>What Claude did shows in Karvon: the Ports window, beside Claude Code.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (tool is "tunnel_open" or "tunnel_list")
            Workbench.ShowToolWindowBesideClaude(ToolWindowId);
        return Task.CompletedTask;
    }

    // ---- IPortsHost ---------------------------------------------------------------------------------------------

    string IPortsHost.ConfigDirectory => Services.Settings.ConfigDirectory;

    IReadOnlyList<string> IPortsHost.ListeningUrls => Workbench.ListeningUrls;

    public event Action? ListeningChanged;

    void IPortsHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IPortsHost.Status(string text) => Workbench.Status(text);
}
