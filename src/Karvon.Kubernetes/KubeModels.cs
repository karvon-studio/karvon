using System.Text.Json;

namespace Karvon.Kubernetes;

/// <summary>A context of kubeconfig's: its cluster, its user, its namespace.</summary>
public sealed record KubeContext(string Name, string Cluster, string User, string? Namespace, bool IsCurrent)
{
    /// <summary>A production cluster, by its name: every change on it asks in red.</summary>
    public bool IsProduction => Name.Contains("prod", StringComparison.OrdinalIgnoreCase) || Cluster.Contains("prod", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

public enum KubeKind
{
    Deployment,
    StatefulSet,
    DaemonSet,
    Job,
    CronJob,
    Pod,
    Service,
    Ingress,
    ConfigMap,
    Secret,
    ArgoApplication,
    FluxKustomization,
    FluxHelmRelease,
}

public static class KubeKinds
{
    /// <summary>The kinds read for a namespace, each time the window reads it.</summary>
    public static readonly KubeKind[] Namespaced =
    [
        KubeKind.Deployment, KubeKind.StatefulSet, KubeKind.DaemonSet, KubeKind.Job, KubeKind.CronJob, KubeKind.Pod,
        KubeKind.Service, KubeKind.Ingress, KubeKind.ConfigMap, KubeKind.Secret,
    ];

    /// <summary>The GitOps kinds, read when the cluster has them (Argo CD's, Flux's).</summary>
    public static readonly KubeKind[] GitOps = [KubeKind.ArgoApplication, KubeKind.FluxKustomization, KubeKind.FluxHelmRelease];

    /// <summary>The resource as kubectl names it, fully (so no other group's of the same name is taken).</summary>
    public static string Resource(this KubeKind kind) => kind switch
    {
        KubeKind.Deployment => "deployments.apps",
        KubeKind.StatefulSet => "statefulsets.apps",
        KubeKind.DaemonSet => "daemonsets.apps",
        KubeKind.Job => "jobs.batch",
        KubeKind.CronJob => "cronjobs.batch",
        KubeKind.Pod => "pods",
        KubeKind.Service => "services",
        KubeKind.Ingress => "ingresses.networking.k8s.io",
        KubeKind.ConfigMap => "configmaps",
        KubeKind.Secret => "secrets",
        KubeKind.ArgoApplication => "applications.argoproj.io",
        KubeKind.FluxKustomization => "kustomizations.kustomize.toolkit.fluxcd.io",
        _ => "helmreleases.helm.toolkit.fluxcd.io",
    };

    /// <summary>The kind of an object kubectl printed (its kind and apiVersion), or null for one the window does not show.</summary>
    public static KubeKind? Of(string kind, string apiVersion) => (kind, apiVersion.Split('/')[0]) switch
    {
        ("Deployment", _) => KubeKind.Deployment,
        ("StatefulSet", _) => KubeKind.StatefulSet,
        ("DaemonSet", _) => KubeKind.DaemonSet,
        ("Job", _) => KubeKind.Job,
        ("CronJob", _) => KubeKind.CronJob,
        ("Pod", _) => KubeKind.Pod,
        ("Service", _) => KubeKind.Service,
        ("Ingress", _) => KubeKind.Ingress,
        ("ConfigMap", _) => KubeKind.ConfigMap,
        ("Secret", _) => KubeKind.Secret,
        ("Application", "argoproj.io") => KubeKind.ArgoApplication,
        ("Kustomization", "kustomize.toolkit.fluxcd.io") => KubeKind.FluxKustomization,
        ("HelmRelease", "helm.toolkit.fluxcd.io") => KubeKind.FluxHelmRelease,
        _ => null,
    };

    /// <summary>What Claude and the user call it: "deployment", "pod", "application".</summary>
    public static KubeKind? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "deployment" or "deployments" or "deploy" => KubeKind.Deployment,
        "statefulset" or "statefulsets" or "sts" => KubeKind.StatefulSet,
        "daemonset" or "daemonsets" or "ds" => KubeKind.DaemonSet,
        "job" or "jobs" => KubeKind.Job,
        "cronjob" or "cronjobs" or "cj" => KubeKind.CronJob,
        "pod" or "pods" or "po" => KubeKind.Pod,
        "service" or "services" or "svc" => KubeKind.Service,
        "ingress" or "ingresses" or "ing" => KubeKind.Ingress,
        "configmap" or "configmaps" or "cm" => KubeKind.ConfigMap,
        "secret" or "secrets" => KubeKind.Secret,
        "application" or "applications" or "app" or "argo" => KubeKind.ArgoApplication,
        "kustomization" or "kustomizations" or "ks" => KubeKind.FluxKustomization,
        "helmrelease" or "helmreleases" or "hr" => KubeKind.FluxHelmRelease,
        _ => null,
    };

    public static string Plural(this KubeKind kind) => kind switch
    {
        KubeKind.Deployment => "Deployments",
        KubeKind.StatefulSet => "Stateful Sets",
        KubeKind.DaemonSet => "Daemon Sets",
        KubeKind.Job => "Jobs",
        KubeKind.CronJob => "Cron Jobs",
        KubeKind.Pod => "Pods",
        KubeKind.Service => "Services",
        KubeKind.Ingress => "Ingresses",
        KubeKind.ConfigMap => "Config Maps",
        KubeKind.Secret => "Secrets",
        KubeKind.ArgoApplication => "Argo CD Applications",
        KubeKind.FluxKustomization => "Flux Kustomizations",
        _ => "Flux Helm Releases",
    };

    public static string Singular(this KubeKind kind) => kind switch
    {
        KubeKind.ArgoApplication => "Application",
        KubeKind.FluxKustomization => "Kustomization",
        KubeKind.FluxHelmRelease => "HelmRelease",
        _ => kind.ToString(),
    };

    /// <summary>The <c>VsIcon</c> that shows it.</summary>
    public static string Icon(this KubeKind kind) => kind switch
    {
        KubeKind.Deployment or KubeKind.StatefulSet or KubeKind.DaemonSet => "DockerStack",
        KubeKind.Pod => "DockerContainer",
        KubeKind.Job => "Start",
        KubeKind.CronJob => "History",
        KubeKind.Service => "DockerNetwork",
        KubeKind.Ingress => "Globe",
        KubeKind.ConfigMap => "json",
        KubeKind.Secret => "EyeOff",
        _ => "GitSync",
    };

    public static bool HasLogs(this KubeKind kind) => kind is KubeKind.Pod or KubeKind.Deployment or KubeKind.StatefulSet or KubeKind.DaemonSet or KubeKind.Job;

    public static bool CanRestart(this KubeKind kind) => kind is KubeKind.Deployment or KubeKind.StatefulSet or KubeKind.DaemonSet;

    public static bool CanScale(this KubeKind kind) => kind is KubeKind.Deployment or KubeKind.StatefulSet;

    public static bool CanForward(this KubeKind kind) => kind is KubeKind.Pod or KubeKind.Service or KubeKind.Deployment or KubeKind.StatefulSet;
}

/// <summary>
/// An object of the cluster's, as the window lists it: its state in a word (Running, CrashLoopBackOff, Synced ·
/// Healthy), how ready (2/3), the rest in a line; whether it is a problem. Secrets' values are never in it.
/// </summary>
public sealed record KubeObject(KubeKind Kind, string Name, string? Namespace, string Status, string Ready, DateTimeOffset? Created, string Detail, bool IsProblem)
{
    /// <summary>As kubectl printed it (a Secret's data left out).</summary>
    public JsonElement Json { get; init; }

    /// <summary>kind/name, as kubectl takes it.</summary>
    public string Ref => $"{Kind.Resource()}/{Name}";

    public string Key => $"{Kind}/{Namespace}/{Name}";

    /// <summary>A workload's pods, by its selector ("app=web,tier=front"); null for the others.</summary>
    public string? Selector { get; init; }

    /// <summary>The ports it says it listens on (a pod's containers', a service's), for a port forward.</summary>
    public IReadOnlyList<int> Ports { get; init; } = [];

    /// <summary>A pod's containers, or a workload's template's.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];
}

/// <summary>An event of an object's (kubectl get events).</summary>
public sealed record KubeEvent(string Type, string Reason, string Message, int Count, DateTimeOffset? Last, string Object);

/// <summary>Reads what kubectl prints (-o json) into what the window shows.</summary>
public static class KubeParse
{
    static readonly HashSet<string> BadReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "CreateContainerConfigError", "CreateContainerError", "InvalidImageName",
        "RunContainerError", "Error", "OOMKilled", "ContainerCannotRun", "DeadlineExceeded", "Evicted",
    };

    public static IReadOnlyList<KubeObject> List(JsonElement list) =>
        list.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray().Select(Object).OfType<KubeObject>()]
        : Object(list) is { } one ? [one] : [];

    public static KubeObject? Object(JsonElement o)
    {
        if (KubeKinds.Of(Str(o, "kind"), Str(o, "apiVersion")) is not { } kind || !o.TryGetProperty("metadata", out var meta))
            return null;
        var name = Str(meta, "name");
        var ns = Str(meta, "namespace") is { Length: > 0 } n ? n : null;
        var created = Date(meta, "creationTimestamp");
        var spec = Prop(o, "spec");
        var status = Prop(o, "status");
        var json = kind == KubeKind.Secret ? WithoutData(o) : o.Clone();
        KubeObject Make(string state, string ready, string detail, bool problem) => new(kind, name, ns, state, ready, created, detail, problem) { Json = json };
        switch (kind)
        {
            case KubeKind.Pod:
            {
                var statuses = Array(status, "containerStatuses").ToList();
                var ready = statuses.Count(c => Bool(c, "ready"));
                var restarts = statuses.Sum(c => Int(c, "restartCount"));
                var reason = statuses.Select(c => Prop(Prop(c, "state"), "waiting") is { ValueKind: JsonValueKind.Object } w ? Str(w, "reason")
                        : Prop(Prop(c, "state"), "terminated") is { ValueKind: JsonValueKind.Object } t ? Str(t, "reason") : "")
                    .FirstOrDefault(r => r.Length > 0 && r != "Completed" && r != "ContainerCreating" && r != "PodInitializing");
                var phase = Str(status, "phase");
                var state = Str(meta, "deletionTimestamp").Length > 0 ? "Terminating"
                    : reason ?? (phase == "Succeeded" ? "Completed" : Str(status, "reason") is { Length: > 0 } podReason ? podReason : phase);
                var total = Array(spec, "containers").Count();
                var problem = BadReasons.Contains(state) || phase == "Failed" || phase == "Running" && ready < total && created < DateTimeOffset.UtcNow.AddMinutes(-2);
                var detail = string.Join(" · ", new[] { restarts > 0 ? $"{restarts} restarts" : "", Str(spec, "nodeName"), Str(status, "podIP") }.Where(s => s.Length > 0));
                return Make(state, $"{ready}/{total}", detail, problem) with
                {
                    Containers = [.. Array(spec, "containers").Select(c => Str(c, "name"))],
                    Ports = [.. Array(spec, "containers").SelectMany(c => Array(c, "ports")).Select(p => Int(p, "containerPort")).Where(p => p > 0).Distinct()],
                };
            }
            case KubeKind.Deployment or KubeKind.StatefulSet or KubeKind.DaemonSet:
            {
                var (ready, desired) = kind == KubeKind.DaemonSet
                    ? (Int(status, "numberReady"), Int(status, "desiredNumberScheduled"))
                    : (Int(status, "readyReplicas"), Prop(spec, "replicas").ValueKind == JsonValueKind.Number ? Int(spec, "replicas") : 1);
                var stalled = Array(status, "conditions").Any(c => Str(c, "reason") == "ProgressDeadlineExceeded");
                var state = desired == 0 ? "Scaled to 0" : stalled ? "Rollout stalled" : ready >= desired ? "Available" : "Updating";
                var template = Prop(Prop(spec, "template"), "spec");
                var images = string.Join(", ", Array(template, "containers").Select(c => Str(c, "image").Split('/')[^1]));
                return Make(state, $"{ready}/{desired}", images, ready < desired && (stalled || created < DateTimeOffset.UtcNow.AddMinutes(-5))) with
                {
                    Selector = Selector(Prop(Prop(spec, "selector"), "matchLabels")),
                    Containers = [.. Array(template, "containers").Select(c => Str(c, "name"))],
                    Ports = [.. Array(template, "containers").SelectMany(c => Array(c, "ports")).Select(p => Int(p, "containerPort")).Where(p => p > 0).Distinct()],
                };
            }
            case KubeKind.Job:
            {
                var conditions = Array(status, "conditions").Where(c => Str(c, "status") == "True").Select(c => Str(c, "type")).ToList();
                var state = conditions.Contains("Failed") ? "Failed" : conditions.Contains("Complete") ? "Complete" : Int(status, "active") > 0 ? "Running" : "Pending";
                var completions = Prop(spec, "completions").ValueKind == JsonValueKind.Number ? Int(spec, "completions") : 1;
                return Make(state, $"{Int(status, "succeeded")}/{completions}", "", state == "Failed") with
                {
                    Selector = Selector(Prop(Prop(spec, "selector"), "matchLabels")),
                    Containers = [.. Array(Prop(Prop(spec, "template"), "spec"), "containers").Select(c => Str(c, "name"))],
                };
            }
            case KubeKind.CronJob:
            {
                var active = Array(status, "active").Count();
                var state = Bool(spec, "suspend") ? "Suspended" : active > 0 ? $"Active ({active})" : "Scheduled";
                var last = Date(status, "lastScheduleTime");
                return Make(state, "", Str(spec, "schedule") + (last is { } at ? $" · last {Ago(at)}" : ""), false);
            }
            case KubeKind.Service:
            {
                var ports = Array(spec, "ports").Select(p =>
                    $"{Int(p, "port")}/{Str(p, "protocol")}" + (Prop(p, "targetPort") is { ValueKind: not JsonValueKind.Undefined } target && target.ToString() != Int(p, "port").ToString() ? $"→{target}" : "")).ToList();
                var external = Array(Prop(status, "loadBalancer"), "ingress").Select(i => Str(i, "ip") is { Length: > 0 } ip ? ip : Str(i, "hostname")).Where(s => s.Length > 0);
                var detail = string.Join(" · ", new[] { Str(spec, "clusterIP"), string.Join(", ", ports), string.Join(", ", external) }.Where(s => s.Length > 0));
                return Make(Str(spec, "type"), "", detail, false) with
                {
                    Selector = Selector(Prop(spec, "selector")),
                    Ports = [.. Array(spec, "ports").Select(p => Int(p, "port")).Where(p => p > 0)],
                };
            }
            case KubeKind.Ingress:
            {
                var hosts = string.Join(", ", Array(spec, "rules").Select(r => Str(r, "host")).Where(h => h.Length > 0));
                var address = string.Join(", ", Array(Prop(status, "loadBalancer"), "ingress").Select(i => Str(i, "ip") is { Length: > 0 } ip ? ip : Str(i, "hostname")));
                return Make(hosts.Length > 0 ? hosts : "*", "", address, false);
            }
            case KubeKind.ConfigMap:
            {
                var keys = Prop(o, "data") is { ValueKind: JsonValueKind.Object } data ? data.EnumerateObject().Count() : 0;
                return Make("", "", $"{keys} key{(keys == 1 ? "" : "s")}", false);
            }
            case KubeKind.Secret:
            {
                var keys = Prop(o, "data") is { ValueKind: JsonValueKind.Object } data ? data.EnumerateObject().Count() : 0;
                return Make("", "", $"{Str(o, "type")} · {keys} key{(keys == 1 ? "" : "s")}", false);
            }
            case KubeKind.ArgoApplication:
            {
                var sync = Str(Prop(status, "sync"), "status");
                var health = Str(Prop(status, "health"), "status");
                var source = Prop(spec, "source") is { ValueKind: JsonValueKind.Object } s ? s : Array(spec, "sources").FirstOrDefault();
                var where = string.Join(" ", new[] { Str(source, "repoURL").Split('/')[^1], Str(source, "path"), Str(source, "targetRevision") is { Length: > 0 } rev ? "@" + rev : "" }.Where(x => x.Length > 0));
                var state = string.Join(" · ", new[] { sync, health }.Where(x => x.Length > 0));
                return Make(state.Length > 0 ? state : "Unknown", "", where, sync == "OutOfSync" || health is "Degraded" or "Missing");
            }
            default:
            {
                // Flux's: its Ready condition says it all.
                var ready = Array(status, "conditions").FirstOrDefault(c => Str(c, "type") == "Ready");
                var state = ready.ValueKind != JsonValueKind.Object ? "Unknown"
                    : Bool(spec, "suspend") ? "Suspended"
                    : Str(ready, "status") == "True" ? "Ready" : Str(ready, "reason") == "Progressing" ? "Reconciling" : "Not ready";
                return Make(state, "", ready.ValueKind == JsonValueKind.Object ? Str(ready, "message") : "", state == "Not ready");
            }
        }
    }

    public static IReadOnlyList<KubeEvent> Events(JsonElement list) =>
    [
        .. Array(list, "items").Select(e => new KubeEvent(Str(e, "type"), Str(e, "reason"), Str(e, "message"), Math.Max(1, Int(e, "count")),
                Date(e, "lastTimestamp") ?? Date(e, "eventTime") ?? Date(Prop(e, "metadata"), "creationTimestamp"),
                $"{Str(Prop(e, "involvedObject"), "kind")}/{Str(Prop(e, "involvedObject"), "name")}"))
            .OrderByDescending(e => e.Last),
    ];

    /// <summary>kubeconfig's contexts (kubectl config view -o json).</summary>
    public static IReadOnlyList<KubeContext> Contexts(JsonElement config)
    {
        var current = Str(config, "current-context");
        return [.. Array(config, "contexts").Select(c =>
        {
            var context = Prop(c, "context");
            return new KubeContext(Str(c, "name"), Str(context, "cluster"), Str(context, "user"), Str(context, "namespace") is { Length: > 0 } ns ? ns : null,
                Str(c, "name") == current);
        })];
    }

    /// <summary>A Secret without its values: its keys stay, each "(hidden)".</summary>
    static JsonElement WithoutData(JsonElement secret)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(secret.GetRawText())!.AsObject();
        foreach (var field in new[] { "data", "stringData" })
            if (node[field] is System.Text.Json.Nodes.JsonObject data)
                foreach (var key in data.Select(p => p.Key).ToList())
                    data[key] = "(hidden)";
        if (node["metadata"]?["annotations"] is System.Text.Json.Nodes.JsonObject annotations)
            annotations.Remove("kubectl.kubernetes.io/last-applied-configuration");
        return JsonSerializer.SerializeToElement(node);
    }

    static string? Selector(JsonElement labels) =>
        labels.ValueKind == JsonValueKind.Object && labels.EnumerateObject().Any() ? string.Join(',', labels.EnumerateObject().Select(l => $"{l.Name}={l.Value.GetString()}")) : null;

    public static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalSeconds < 60 ? $"{Math.Max(0, (int)span.TotalSeconds)}s ago"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>"4d", "3h", "12m": an object's age, as kubectl get says it.</summary>
    public static string Age(DateTimeOffset? created)
    {
        if (created is not { } at)
            return "";
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalMinutes < 1 ? $"{Math.Max(0, (int)span.TotalSeconds)}s" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";
    }

    internal static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : "";

    internal static int Int(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : 0;

    internal static bool Bool(JsonElement e, string name) => Prop(e, name).ValueKind == JsonValueKind.True;

    internal static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    internal static DateTimeOffset? Date(JsonElement e, string name) => DateTimeOffset.TryParse(Str(e, name), out var at) ? at : null;
}
