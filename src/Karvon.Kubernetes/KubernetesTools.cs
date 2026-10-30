using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Kubernetes.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Kubernetes;

/// <summary>
/// Claude Code's tools for the user's Kubernetes clusters, through their own kubectl and kubeconfig, in the context the
/// Kubernetes window has (or one named): what runs and how it stands, an object described, a pod's logs, the events;
/// and, asking the user each time (in red on a production cluster), a workload restarted or scaled, a pod deleted.
/// </summary>
public static class KubernetesClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<KubernetesViewModel> window) =>
    [
        new K8sContextsTool(window), new K8sGetTool(window), new K8sDescribeTool(window), new K8sLogsTool(window), new K8sEventsTool(window),
        new K8sRestartTool(window), new K8sScaleTool(window), new K8sDeletePodTool(window),
    ];

    internal static string Row(KubeObject o) =>
        $"{o.Kind.Singular().ToLowerInvariant()}/{o.Name}" + (o.Namespace is { } ns ? $" ({ns})" : "")
        + (o.Status.Length > 0 ? $" [{o.Status}]" : "") + (o.Ready.Length > 0 ? $" {o.Ready} ready" : "")
        + $" {KubeParse.Age(o.Created)}" + (o.Detail.Length > 0 ? $" · {o.Detail}" : "") + (o.IsProblem ? " (problem)" : "");
}

abstract class KubernetesTool(Func<KubernetesViewModel> window) : IClaudeTool
{
    protected KubernetesViewModel Window => window();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    /// <summary>The context a call works in: the one it names, else the window's.</summary>
    public string? Target(JsonElement input) => ContextName(input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is KubeException or InvalidOperationException or JsonException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    protected static string Required(JsonElement input, string name) =>
        Str(input, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"'{name}' is required.");

    /// <summary>The context named (one of kubeconfig's), else the window's.</summary>
    protected KubeContext ContextOf(JsonElement input) =>
        Str(input, "context") is { Length: > 0 } name
            ? Window.Contexts.FirstOrDefault(c => c.Name == name) ?? throw new KubeException($"kubeconfig has no context {name}: see k8s_contexts.")
            : Window.ContextOrThrow();

    /// <summary>The namespace given, else the window's (its context's when it shows every one).</summary>
    protected string NamespaceOf(JsonElement input, KubeContext context) =>
        Str(input, "namespace") is { Length: > 0 } ns ? ns : context == Window.Context && Window.NamespaceName is { } shown ? shown : context.Namespace ?? "default";

    protected bool IsProduction(JsonElement input) =>
        (Str(input, "context") is { Length: > 0 } name ? Window.Contexts.FirstOrDefault(c => c.Name == name) : Window.Context)?.IsProduction == true;

    protected string ContextName(JsonElement input) => Str(input, "context") is { Length: > 0 } name ? name : Window.Context?.Name ?? "the cluster";

    protected static KubeKind KindOf(JsonElement input, KubeKind? fallback = null) =>
        KubeKinds.Parse(Str(input, "kind")) ?? fallback ?? throw new InvalidOperationException($"'kind' is not one Karvon knows: {Str(input, "kind")}.");

    protected Kubectl Kubectl => Window.Kubectl ?? throw new KubeException("kubectl was not found on this machine.");

    protected static JsonObject ContextProperty => new() { ["type"] = "string", ["description"] = "A kubeconfig context (default: the Kubernetes window's)." };

    protected static JsonObject NamespaceProperty => new() { ["type"] = "string", ["description"] = "The namespace (default: the window's)." };

    protected static JsonObject KindProperty(string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray("deployment", "statefulset", "daemonset", "job", "cronjob", "pod", "service", "ingress", "configmap", "secret",
            "application", "kustomization", "helmrelease"),
        ["description"] = description,
    };
}

/// <summary><c>k8s_contexts</c>: kubeconfig's contexts.</summary>
sealed class K8sContextsTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_contexts";

    public override string Description =>
        "Lists the user's Kubernetes contexts (their kubeconfig's): cluster, user, namespace; which one kubectl is set to and which one the "
        + "Kubernetes window (and these tools, by default) use.";

    public override JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Kubernetes Contexts", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Window.Contexts.Count == 0)
            await Window.LoadAsync();
        if (Window.Contexts.Count == 0)
            return ClaudeToolOutput.Error(Window.Message ?? "kubeconfig has no contexts.");
        return new ClaudeToolOutput(string.Join("\n", Window.Contexts.Select(c =>
            $"{c.Name}: cluster {c.Cluster}, user {c.User}" + (c.Namespace is { } ns ? $", namespace {ns}" : "")
            + (c.IsCurrent ? " (kubectl's current)" : "") + (c == Window.Context ? $" (Karvon's, namespace {Window.NamespaceName ?? "all"})" : "")
            + (c.IsProduction ? " (production)" : ""))));
    }
}

/// <summary><c>k8s_get</c>: what runs in a namespace, or one object, and how it stands.</summary>
sealed class K8sGetTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_get";

    public override string Description =>
        "Lists Kubernetes objects and how they stand (state, ready, age, images, restarts; problems marked): of a kind, or the "
        + "workloads, pods and services when none is given; in a namespace or every one; one object by its name, with its conditions.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = KindProperty("What to list (default: deployments, stateful sets, daemon sets, jobs, pods and services)."),
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "One object's name (needs kind)." },
            ["namespace"] = NamespaceProperty,
            ["all_namespaces"] = new JsonObject { ["type"] = "boolean" },
            ["context"] = ContextProperty,
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Kubernetes", Str(input, "kind") ?? Str(input, "name"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var context = ContextOf(input);
        var all = input.TryGetProperty("all_namespaces", out var a) && a.ValueKind == JsonValueKind.True;
        var ns = NamespaceOf(input, context);
        if (Str(input, "name") is { Length: > 0 } name)
        {
            var kind = KindOf(input);
            var (one, _) = await Kubectl.JsonAsync([.. Kubectl.In(context.Name, ns), "get", kind.Resource(), name], cancellationToken);
            using (one)
            {
                var o = KubeParse.Object(one.RootElement) ?? throw new KubeException($"{kind.Singular()} {name} could not be read.");
                var text = new StringBuilder(KubernetesClaudeTools.Row(o));
                foreach (var c in KubeParse.Array(KubeParse.Prop(o.Json, "status"), "conditions"))
                    text.Append($"\n  condition {KubeParse.Str(c, "type")}={KubeParse.Str(c, "status")} {KubeParse.Str(c, "reason")} {KubeParse.Str(c, "message")}".TrimEnd());
                if (o.Containers.Count > 0)
                    text.Append("\n  containers: " + string.Join(", ", o.Containers));
                return new ClaudeToolOutput(text.ToString());
            }
        }
        KubeKind[] kinds = Str(input, "kind") is { Length: > 0 } ? [KindOf(input)]
            : [KubeKind.Deployment, KubeKind.StatefulSet, KubeKind.DaemonSet, KubeKind.Job, KubeKind.Pod, KubeKind.Service];
        var gitOps = kinds.All(k => KubeKinds.GitOps.Contains(k));
        string[] scope = all || gitOps ? [.. Kubectl.In(context.Name, null), "get", "--all-namespaces"] : [.. Kubectl.In(context.Name, ns), "get"];
        var (json, warning) = await Kubectl.JsonAsync([.. scope, string.Join(',', kinds.Select(k => k.Resource()))], cancellationToken);
        using (json)
        {
            var objects = KubeParse.List(json.RootElement);
            var where = $"{context.Name}, {(all || gitOps ? "every namespace" : "namespace " + ns)}";
            return new ClaudeToolOutput(objects.Count == 0
                ? $"Nothing of that in {where}." + (warning is null ? "" : $" ({warning})")
                : $"{where}:\n" + string.Join("\n", objects.Select(KubernetesClaudeTools.Row)) + (warning is null ? "" : $"\n(kubectl: {warning})"));
        }
    }
}

/// <summary><c>k8s_describe</c>: kubectl describe of an object.</summary>
sealed class K8sDescribeTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_describe";

    public override string Description =>
        "Describes a Kubernetes object as kubectl describe does: its spec and state, its containers, conditions and recent events "
        + "(a secret's keys and sizes, never its values).";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = KindProperty("Its kind."), ["name"] = new JsonObject { ["type"] = "string" }, ["namespace"] = NamespaceProperty, ["context"] = ContextProperty,
        },
        ["required"] = new JsonArray("kind", "name"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Describe", $"{Str(input, "kind")}/{Str(input, "name")}", "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var context = ContextOf(input);
        var text = await Kubectl.OutputAsync([.. Kubectl.In(context.Name, NamespaceOf(input, context)), "describe", KindOf(input).Resource(), Required(input, "name")], cancellationToken);
        return new ClaudeToolOutput(Redaction.Text(text)) { Outside = $"the description of {Str(input, "kind")}/{Str(input, "name")}" };
    }
}

/// <summary><c>k8s_logs</c>: a pod's (or a workload's) last log lines.</summary>
sealed class K8sLogsTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_logs";

    public override string Description =>
        "Returns the last log lines of a pod (a container's, or all of them), or of a workload's pods (kind and name), or of the run "
        + "before a crash (previous). Secrets in them are hidden.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The pod's name, or the workload's." },
            ["kind"] = KindProperty("pod (default), deployment, statefulset, daemonset or job."),
            ["container"] = new JsonObject { ["type"] = "string" },
            ["previous"] = new JsonObject { ["type"] = "boolean", ["description"] = "The run before the last (a crash's)." },
            ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "How many lines (default 200)." },
            ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Only newer lines: 10m, 1h." },
            ["namespace"] = NamespaceProperty, ["context"] = ContextProperty,
        },
        ["required"] = new JsonArray("name"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Logs", Str(input, "name"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var context = ContextOf(input);
        var kind = KindOf(input, KubeKind.Pod);
        var name = Required(input, "name");
        List<string> args = [.. Kubectl.In(context.Name, NamespaceOf(input, context)), "logs", $"--tail={Int(input, "tail", 200, 1, 2000)}"];
        if (kind == KubeKind.Pod)
            args.Add(name);
        else
            args.AddRange([$"{kind.Resource()}/{name}", "--prefix", "--max-log-requests=10"]);
        if (Str(input, "container") is { Length: > 0 } container)
            args.AddRange(["-c", container]);
        else
            args.Add("--all-containers");
        if (input.TryGetProperty("previous", out var previous) && previous.ValueKind == JsonValueKind.True)
            args.Add("--previous");
        if (Str(input, "since") is { Length: > 0 } since)
            args.Add($"--since={since}");
        var text = await Kubectl.OutputAsync(args, cancellationToken);
        return text.Length == 0
            ? new ClaudeToolOutput($"{name} wrote nothing{(Str(input, "since") is { } s ? $" in {s}" : "")}.")
            : new ClaudeToolOutput(Redaction.Text(text)) { Outside = $"the logs of {name}" };
    }
}

/// <summary><c>k8s_events</c>: a namespace's (or an object's) events, warnings first.</summary>
sealed class K8sEventsTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_events";

    public override string Description =>
        "Returns a namespace's recent Kubernetes events (or one object's): back-offs, failed pulls and probes, evictions, scheduling; newest first.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "One object's events only." },
            ["warnings_only"] = new JsonObject { ["type"] = "boolean" },
            ["namespace"] = NamespaceProperty, ["context"] = ContextProperty,
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Events", Str(input, "name") ?? Str(input, "namespace"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var context = ContextOf(input);
        List<string> args = [.. Kubectl.In(context.Name, NamespaceOf(input, context)), "get", "events"];
        var selectors = new List<string>();
        if (Str(input, "name") is { Length: > 0 } name)
            selectors.Add($"involvedObject.name={name}");
        if (input.TryGetProperty("warnings_only", out var w) && w.ValueKind == JsonValueKind.True)
            selectors.Add("type=Warning");
        if (selectors.Count > 0)
            args.Add("--field-selector=" + string.Join(',', selectors));
        var (json, _) = await Kubectl.JsonAsync(args, cancellationToken);
        using (json)
        {
            var events = KubeParse.Events(json.RootElement).Take(60).ToList();
            return events.Count == 0 ? new ClaudeToolOutput("No events.") : new ClaudeToolOutput(string.Join("\n", events.Select(e =>
                $"{(e.Last is { } at ? KubeParse.Ago(at) : "")} {e.Type} {e.Reason} {e.Object}{(e.Count > 1 ? $" ×{e.Count}" : "")}: {Redaction.Text(e.Message)}".Trim())))
            {
                Outside = "the cluster's events",
            };
        }
    }
}

/// <summary><c>k8s_restart</c>: a workload's pods replaced (asks).</summary>
sealed class K8sRestartTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_restart";

    public override string Description => "Restarts a deployment, stateful set or daemon set (kubectl rollout restart). The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = KindProperty("deployment (default), statefulset or daemonset."), ["name"] = new JsonObject { ["type"] = "string" },
            ["namespace"] = NamespaceProperty, ["context"] = ContextProperty,
        },
        ["required"] = new JsonArray("name"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(IsProduction(input) ? ToolAccess.Destructive : ToolAccess.Write, "Restart", Str(input, "name"),
            $"Allow Claude to restart {Str(input, "kind") ?? "deployment"} {Str(input, "name")} in {ContextName(input)}?")
        {
            Warning = IsProduction(input) ? "It is a production cluster: its pods are replaced one by one." : "Its pods are replaced one by one.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var kind = KindOf(input, KubeKind.Deployment);
        if (!kind.CanRestart())
            return ClaudeToolOutput.Error($"A {kind.Singular()} is not restarted: a deployment, stateful set or daemon set is.");
        var context = ContextOf(input);
        return new ClaudeToolOutput(await Kubectl.OutputAsync([.. Kubectl.In(context.Name, NamespaceOf(input, context)), "rollout", "restart", $"{kind.Resource()}/{Required(input, "name")}"],
            cancellationToken));
    }
}

/// <summary><c>k8s_scale</c>: how many pods a workload runs (asks).</summary>
sealed class K8sScaleTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_scale";

    public override string Description => "Scales a deployment or stateful set to a number of pods (0 stops it). The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = KindProperty("deployment (default) or statefulset."), ["name"] = new JsonObject { ["type"] = "string" },
            ["replicas"] = new JsonObject { ["type"] = "integer" }, ["namespace"] = NamespaceProperty, ["context"] = ContextProperty,
        },
        ["required"] = new JsonArray("name", "replicas"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(IsProduction(input) || Int(input, "replicas", 1, 0, 1000) == 0 ? ToolAccess.Destructive : ToolAccess.Write, "Scale", Str(input, "name"),
            $"Allow Claude to scale {Str(input, "name")} to {Int(input, "replicas", 1, 0, 1000)} in {ContextName(input)}?")
        {
            Warning = Int(input, "replicas", 1, 0, 1000) == 0 ? "No pod of it runs until it is scaled up again." : null,
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var kind = KindOf(input, KubeKind.Deployment);
        if (!kind.CanScale())
            return ClaudeToolOutput.Error($"A {kind.Singular()} is not scaled: a deployment or stateful set is.");
        if (!input.TryGetProperty("replicas", out var r) || r.ValueKind != JsonValueKind.Number)
            return ClaudeToolOutput.Error("'replicas' is required.");
        var context = ContextOf(input);
        return new ClaudeToolOutput(await Kubectl.OutputAsync([.. Kubectl.In(context.Name, NamespaceOf(input, context)), "scale", $"{kind.Resource()}/{Required(input, "name")}",
            $"--replicas={Int(input, "replicas", 1, 0, 1000)}"], cancellationToken));
    }
}

/// <summary><c>k8s_delete_pod</c>: a pod deleted (its controller makes another; asks).</summary>
sealed class K8sDeletePodTool(Func<KubernetesViewModel> window) : KubernetesTool(window)
{
    public override string Name => "k8s_delete_pod";

    public override string Description =>
        "Deletes a pod: its deployment (or other controller) makes a new one; a pod of none is gone. The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["namespace"] = NamespaceProperty, ["context"] = ContextProperty },
        ["required"] = new JsonArray("name"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Destructive, "Delete Pod", Str(input, "name"), $"Allow Claude to delete the pod {Str(input, "name")} in {ContextName(input)}?")
        {
            Warning = "Its controller makes a new one, when it has one; a pod of none is gone.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var context = ContextOf(input);
        return new ClaudeToolOutput(await Kubectl.OutputAsync([.. Kubectl.In(context.Name, NamespaceOf(input, context)), "delete", "pod", Required(input, "name"), "--wait=false"],
            cancellationToken));
    }
}
