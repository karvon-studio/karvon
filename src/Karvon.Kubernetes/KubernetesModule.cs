using System.Text.Json;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Kubernetes.ViewModels;
using Karvon.Kubernetes.Views;
using Karvon.Modules;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Kubernetes;

/// <summary>
/// Kubernetes: the user's clusters through their own kubectl and kubeconfig, in a tool window (see
/// <see cref="KubernetesViewModel"/>); an object opens as a page with its logs, YAML and a shell. Nothing is asked of a
/// cluster before the window shows (or Claude asks). Claude Code gets the Kubernetes tools.
/// </summary>
public sealed partial class KubernetesModule : KarvonModule, IKubernetesHost
{
    public const string ToolWindowId = "Kubernetes";

    IClaudeService? _claude;

    public override string Id => "kubernetes";

    public override string Name => "Kubernetes";

    public override string Icon => "kubernetes";

    public override string Description =>
        "Your clusters through your own kubectl and kubeconfig: a context's namespaces, deployments, stateful and daemon sets, jobs, pods, "
        + "services, ingresses, config maps and secrets (their keys), Argo CD's and Flux's apps; an object's logs, YAML and a shell; restart, "
        + "scale, roll back, port forward, sync; Claude Code's Kubernetes tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Kubernetes", "_Kubernetes", "ToolKubernetes", DockSide.Right, () => new KubernetesView { DataContext = EnsureWindow() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<KubeDocument>(document => new KubePageView { DataContext = document.Page })];

    public override IDataTemplate OverlayTemplate => Template<KubernetesViewModel>(_ => new KubernetesOverlay());

    [ObservableProperty]
    public partial KubernetesViewModel? Window { get; private set; }

    /// <summary>How kubectl is found (the tests' fake one).</summary>
    internal Func<Kubectl?> Find { get; set; } = Kubectl.Find;

    public KubernetesViewModel EnsureWindow()
    {
        if (Window is null)
            Overlay = Window = new KubernetesViewModel(this, Find());
        return Window;
    }

    protected override void Started() => _claude = Workbench.Service<IClaudeService>();

    public override void Dispose()
    {
        Window?.Dispose();
        Overlay = Window = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => KubernetesClaudeTools.Create(EnsureWindow);

    /// <summary>kubectl's reading and the changes the tools make: Karvon's tools instead (the user sees them in its window).</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("kubectl",
            @"kubectl(\s+(-n|--namespace|--context)(\s+|=)\S+)*\s+(get|describe|logs|events|rollout\s+restart|scale|delete\s+pods?)\b", Use),
    ];

    const string Use =
        "Karvon reaches the user's clusters through their kubectl, in the context its Kubernetes window has: use mcp__karvon__k8s_get, "
        + "k8s_describe, k8s_logs and k8s_events, and k8s_restart, k8s_scale or k8s_delete_pod (the user approves them; k8s_contexts lists "
        + "the contexts). For what they do not do, run kubectl again.";

    /// <summary>What Claude looked at shows in Karvon: the window, or the object's page.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (output.IsError || Window is not { } window)
            return Task.CompletedTask;
        if (tool is "k8s_contexts" or "k8s_events" || tool == "k8s_get" && Str(input, "name") is null)
            Workbench.ShowToolWindowBesideClaude(ToolWindowId);
        else if (tool is "k8s_get" or "k8s_describe" or "k8s_logs" or "k8s_restart" or "k8s_scale" && Str(input, "name") is { } name
                 && Str(input, "context") is null && KubeKinds.Parse(Str(input, "kind") ?? (tool == "k8s_logs" ? "pod" : "deployment")) is { } kind)
            _ = OpenAsync(window, kind, name, Str(input, "namespace"));
        return Task.CompletedTask;
    }

    static async Task OpenAsync(KubernetesViewModel window, KubeKind kind, string name, string? ns)
    {
        if (window.Objects.FirstOrDefault(o => o.Kind == kind && o.Name == name && (ns is null || o.Namespace == ns)) is { } known)
        {
            window.Open(known);
            return;
        }
        try
        {
            window.Open(await window.GetAsync(kind, name, ns));
        }
        catch (KubeException)
        {
        }
    }

    // ---- IKubernetesHost --------------------------------------------------------------------------------------

    string IKubernetesHost.ConfigDirectory => Services.Settings.ConfigDirectory;

    IEnumerable<DocumentTab> IKubernetesHost.Documents => Workbench.Documents;

    void IKubernetesHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IKubernetesHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IKubernetesHost.Status(string text) => Workbench.Status(text);

    void IKubernetesHost.AskClaude(string prompt) => _claude?.Ask(prompt);
}
