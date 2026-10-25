using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Terminal;
using Karvon.ViewModels;
using static Karvon.Kubernetes.KubeParse;

namespace Karvon.Kubernetes.ViewModels;

/// <summary>A line of the overview: what and its value.</summary>
public sealed record KubeFact(string Name, string Value);

/// <summary>A container of a pod (or a workload's template): its image, and, for a pod, its state.</summary>
public sealed record KubeContainerRow(string Name, string Image, string State, bool Ready, int Restarts)
{
    public string Detail => State.Length == 0 ? Image : $"{Image} · {State}{(Restarts > 0 ? $" · {Restarts} restarts" : "")}";
}

public sealed record KubeConditionRow(string Type, string Status, string Reason, string Message)
{
    public bool IsTrue => Status == "True";

    public string Text => string.Join(" · ", new[] { Reason, Message }.Where(s => s.Length > 0));
}

public sealed record KubeEventRow(KubeEvent Event)
{
    public bool IsWarning => Event.Type == "Warning";

    public string Heading => $"{Event.Reason}{(Event.Count > 1 ? $" ×{Event.Count}" : "")}";

    public string When => Event.Last is { } at ? Ago(at) : "";

    public string Message => Event.Message;
}

/// <summary>A choice of the logs' container box (null: all of them).</summary>
public sealed record ContainerChoice(string? Name)
{
    public override string ToString() => Name ?? "All Containers";
}

/// <summary>
/// An object's page: its state and facts, its containers, conditions and events, a workload's or a service's pods;
/// the logs (following, a container's or all, the previous run's), its YAML (a secret's keys only), a shell in a pod;
/// and what is done to it: restart, scale, roll back, delete, a port forwarded, a cron job run now or suspended, a
/// GitOps app synced, Claude asked about it.
/// </summary>
public sealed partial class KubePageViewModel : ObservableObject, IDisposable
{
    readonly KubernetesViewModel _window;
    KubeStream? _logs;
    bool _disposed;

    public KubePageViewModel(KubernetesViewModel window, KubeContext context, KubeObject o)
    {
        _window = window;
        Context = context;
        Object = o;
        LoadFacts();
        Containers = [new ContainerChoice(null), .. o.Containers.Select(c => new ContainerChoice(c))];
        Container = o.Kind == KubeKind.Pod && o.Containers.Count == 1 ? Containers[1] : Containers[0];
    }

    public KubeContext Context { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Facts), nameof(Status), nameof(IsProblem), nameof(IsGood), nameof(Ready), nameof(HasReady))]
    public partial KubeObject Object { get; set; }

    partial void OnObjectChanged(KubeObject value) => LoadFacts();

    public string Heading => $"{Object.Kind.Singular()} {Object.Name}";

    public string Facts => string.Join(" · ", new[] { Object.Namespace ?? "", Created, Context.Name }.Where(s => s.Length > 0));

    string Created => Object.Created is { } at ? "created " + Ago(at) : "";

    public string Status => Object.Status;

    public bool IsProblem => Object.IsProblem;

    public bool IsGood => !Object.IsProblem && Object.Status is "Running" or "Available" or "Complete" or "Ready" or "Synced · Healthy";

    public string Ready => Object.Ready.Length > 0 ? $"{Object.Ready} ready" : "";

    public bool HasReady => Object.Ready.Length > 0;

    public bool HasLogs => Object.Kind.HasLogs();

    public bool CanRestart => Object.Kind.CanRestart();

    public bool CanScale => Object.Kind.CanScale();

    public bool CanUndo => Object.Kind == KubeKind.Deployment;

    public bool CanForward => Object.Kind.CanForward();

    public bool CanDelete => Object.Kind is KubeKind.Pod or KubeKind.Job;

    public bool IsCronJob => Object.Kind == KubeKind.CronJob;

    public bool IsSuspended => Object.Status == "Suspended";

    public bool IsArgo => Object.Kind == KubeKind.ArgoApplication;

    public bool CanSync => Object.Kind is KubeKind.ArgoApplication or KubeKind.FluxKustomization or KubeKind.FluxHelmRelease;

    public bool HasTerminal => Object.Kind == KubeKind.Pod;

    // ---- The overview ---------------------------------------------------------------------------------------------

    public ObservableCollection<KubeFact> Info { get; } = [];

    public ObservableCollection<KubeContainerRow> ContainerRows { get; } = [];

    public ObservableCollection<KubeConditionRow> Conditions { get; } = [];

    public ObservableCollection<KubeEventRow> Events { get; } = [];

    /// <summary>A workload's pods (a service's: those it sends to).</summary>
    public ObservableCollection<KubeRow> Pods { get; } = [];

    public bool HasContainers => ContainerRows.Count > 0;

    public bool HasConditions => Conditions.Count > 0;

    public bool HasEvents => Events.Count > 0;

    public bool HasPods => Pods.Count > 0;

    void LoadFacts()
    {
        var o = Object;
        var json = o.Json;
        var spec = Prop(json, "spec");
        var status = Prop(json, "status");
        var meta = Prop(json, "metadata");
        Info.Clear();
        void Fact(string name, string value)
        {
            if (value.Length > 0)
                Info.Add(new KubeFact(name, value));
        }
        switch (o.Kind)
        {
            case KubeKind.Pod:
                Fact("Node", Str(spec, "nodeName"));
                Fact("Pod IP", Str(status, "podIP"));
                Fact("Phase", Str(status, "phase"));
                Fact("Controlled by", string.Join(", ", Array(meta, "ownerReferences").Select(r => $"{Str(r, "kind")}/{Str(r, "name")}")));
                Fact("Service account", Str(spec, "serviceAccountName"));
                break;
            case KubeKind.Deployment or KubeKind.StatefulSet or KubeKind.DaemonSet:
                Fact("Ready", o.Ready);
                Fact("Up to date", Int(status, "updatedReplicas") is var updated and > 0 ? updated.ToString() : "");
                Fact("Strategy", Str(Prop(spec, "strategy"), "type") is { Length: > 0 } strategy ? strategy : Str(Prop(spec, "updateStrategy"), "type"));
                Fact("Selector", o.Selector ?? "");
                break;
            case KubeKind.Job:
                Fact("Succeeded", o.Ready);
                Fact("Started", Date(status, "startTime") is { } started ? Ago(started) : "");
                Fact("Finished", Date(status, "completionTime") is { } done ? Ago(done) : "");
                break;
            case KubeKind.CronJob:
                Fact("Schedule", Str(spec, "schedule"));
                Fact("Last run", Date(status, "lastScheduleTime") is { } last ? Ago(last) : "never");
                Fact("Suspended", Bool(spec, "suspend") ? "yes" : "no");
                break;
            case KubeKind.Service:
                Fact("Type", Str(spec, "type"));
                Fact("Cluster IP", Str(spec, "clusterIP"));
                Fact("Ports", string.Join(", ", Array(spec, "ports").Select(p => $"{Str(p, "name")} {Int(p, "port")}/{Str(p, "protocol")} → {Prop(p, "targetPort")}".Trim())));
                Fact("Selector", o.Selector ?? "");
                break;
            case KubeKind.Ingress:
                foreach (var rule in Array(spec, "rules"))
                    foreach (var path in Array(Prop(rule, "http"), "paths"))
                    {
                        var backend = Prop(Prop(path, "backend"), "service");
                        Fact((Str(rule, "host") is { Length: > 0 } host ? host : "*") + Str(path, "path"),
                            $"{Str(backend, "name")}:{(Prop(Prop(backend, "port"), "number") is { ValueKind: JsonValueKind.Number } n ? n.ToString() : Str(Prop(backend, "port"), "name"))}");
                    }
                Fact("Address", o.Detail);
                break;
            case KubeKind.ConfigMap:
                if (Prop(json, "data") is { ValueKind: JsonValueKind.Object } data)
                    foreach (var item in data.EnumerateObject())
                        Fact(item.Name, item.Value.ToString().Length > 300 ? item.Value.ToString()[..300] + "…" : item.Value.ToString());
                break;
            case KubeKind.Secret:
                Fact("Type", Str(json, "type"));
                if (Prop(json, "data") is { ValueKind: JsonValueKind.Object } keys)
                    foreach (var item in keys.EnumerateObject())
                        Fact(item.Name, "(hidden)");
                break;
            case KubeKind.ArgoApplication:
            {
                var source = Prop(spec, "source") is { ValueKind: JsonValueKind.Object } s ? s : Array(spec, "sources").FirstOrDefault();
                Fact("Repository", Str(source, "repoURL"));
                Fact("Path", Str(source, "path") is { Length: > 0 } p ? p : Str(source, "chart"));
                Fact("Revision", Str(source, "targetRevision"));
                Fact("Synced to", Str(Prop(status, "sync"), "revision"));
                Fact("Destination", $"{Str(Prop(spec, "destination"), "server")}{Str(Prop(spec, "destination"), "name")} {Str(Prop(spec, "destination"), "namespace")}".Trim());
                Fact("Out of sync", string.Join(", ", Array(status, "resources").Where(r => Str(r, "status") == "OutOfSync").Select(r => $"{Str(r, "kind")}/{Str(r, "name")}")));
                break;
            }
            default:
                Fact("Source", $"{Str(Prop(spec, "sourceRef"), "kind")}/{Str(Prop(spec, "sourceRef"), "name")}".Trim('/'));
                Fact("Path", Str(spec, "path"));
                Fact("Interval", Str(spec, "interval"));
                Fact("Applied", Str(status, "lastAppliedRevision"));
                break;
        }
        Fact("Labels", Prop(meta, "labels") is { ValueKind: JsonValueKind.Object } labels ? string.Join(", ", labels.EnumerateObject().Select(l => $"{l.Name}={l.Value}")) : "");

        ContainerRows.Clear();
        if (o.Kind == KubeKind.Pod)
        {
            var states = Array(status, "containerStatuses").ToDictionary(c => Str(c, "name"));
            foreach (var c in Array(spec, "containers"))
            {
                states.TryGetValue(Str(c, "name"), out var st);
                var state = Prop(st, "state") is { ValueKind: JsonValueKind.Object } s ? s.EnumerateObject().FirstOrDefault() : default;
                var word = state.Name is null ? "" : Str(state.Value, "reason") is { Length: > 0 } reason ? reason : char.ToUpperInvariant(state.Name[0]) + state.Name[1..];
                ContainerRows.Add(new KubeContainerRow(Str(c, "name"), Str(c, "image"), word, Bool(st, "ready"), Int(st, "restartCount")));
            }
        }
        else
            foreach (var c in Array(Prop(Prop(spec, "template"), "spec"), "containers"))
                ContainerRows.Add(new KubeContainerRow(Str(c, "name"), Str(c, "image"), "", false, 0));

        Conditions.Clear();
        foreach (var c in Array(status, "conditions"))
            Conditions.Add(new KubeConditionRow(Str(c, "type"), Str(c, "status"), Str(c, "reason"), Str(c, "message")));
        OnPropertyChanged(nameof(HasContainers));
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(IsSuspended));
    }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The object, its events and its pods read again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        IsBusy = true;
        try
        {
            Object = await _window.GetAsync(Object.Kind, Object.Name, Object.Namespace);
            var kubectl = _window.Kubectl!;
            var (events, _) = await kubectl.JsonAsync([.. _window.At(Object), "get", "events",
                $"--field-selector=involvedObject.name={Object.Name},involvedObject.kind={Object.Kind.Singular()}"]);
            using (events)
            {
                Events.Clear();
                foreach (var e in KubeParse.Events(events.RootElement).Take(30))
                    Events.Add(new KubeEventRow(e));
            }
            Pods.Clear();
            if (Object.Selector is { } selector && Object.Kind != KubeKind.Pod)
            {
                var (pods, _) = await kubectl.JsonAsync([.. _window.At(Object), "get", "pods", "-l", selector]);
                using (pods)
                    foreach (var pod in KubeParse.List(pods.RootElement))
                        Pods.Add(new KubeRow(pod, false));
            }
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(HasPods));
            Error = null;
            if (IsYaml)
                await LoadYamlAsync();
        }
        catch (KubeException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void OpenPod(KubeRow? row)
    {
        if (row is not null)
            _window.Open(row.Object);
    }

    // ---- The tabs -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsLogs), nameof(IsYaml), nameof(IsTerminal))]
    public partial string Tab { get; set; } = "overview";

    public bool IsOverview => Tab == "overview";

    public bool IsLogs => Tab == "logs";

    public bool IsYaml => Tab == "yaml";

    public bool IsTerminal => Tab == "terminal";

    [RelayCommand]
    void ShowTab(string? tab) => Tab = tab ?? "overview";

    partial void OnTabChanged(string value)
    {
        if (value == "logs" && _logs is null)
            StartLogs();
        else if (value == "yaml")
            _ = LoadYamlAsync();
        else if (value == "terminal")
            OpenShell();
    }

    /// <summary>The logs, following: of a pod's container (or all of them), or of a workload's pods, each line said whose.</summary>
    public OutputChannel Logs { get; } = new("Logs");

    public IReadOnlyList<ContainerChoice> Containers { get; }

    public bool HasManyContainers => Object.Containers.Count > 1;

    [ObservableProperty]
    public partial ContainerChoice Container { get; set; }

    partial void OnContainerChanged(ContainerChoice value)
    {
        if (_logs is not null)
            StartLogs();
    }

    /// <summary>The run before the last (a container that crashed).</summary>
    [ObservableProperty]
    public partial bool Previous { get; set; }

    partial void OnPreviousChanged(bool value) => StartLogs();

    internal const int LogTail = 500;

    void StartLogs()
    {
        _logs?.Dispose();
        _logs = null;
        Logs.Clear();
        if (_disposed || _window.Kubectl is not { } kubectl)
            return;
        List<string> args = [.. _window.At(Object), "logs", $"--tail={LogTail}"];
        if (!Previous)
            args.Add("--follow");
        else
            args.Add("--previous");
        if (Object.Kind == KubeKind.Pod)
        {
            args.Add(Object.Name);
            if (Container.Name is { } container)
                args.AddRange(["-c", container]);
            else
                args.AddRange(["--all-containers", "--prefix"]);
        }
        else
            args.AddRange([Object.Ref, "--all-containers", "--prefix", "--max-log-requests=10"]);
        try
        {
            _logs = kubectl.Stream(args, Logs.Append, code =>
            {
                if (code != 0)
                    Logs.Append($"\u001b[90m[kubectl logs exited with code {code}]\u001b[0m");
            });
        }
        catch (KubeException ex)
        {
            Logs.Append(ex.Message);
        }
    }

    [RelayCommand]
    void ClearLogs() => Logs.Clear();

    [ObservableProperty]
    public partial string Yaml { get; private set; } = "";

    /// <summary>Its YAML as kubectl prints it; a secret described (its keys and their sizes, never their values).</summary>
    async Task LoadYamlAsync()
    {
        try
        {
            var text = await _window.Kubectl!.OutputAsync(Object.Kind == KubeKind.Secret
                ? [.. _window.At(Object), "describe", Object.Ref]
                : [.. _window.At(Object), "get", Object.Ref, "-o", "yaml"]);
            Yaml = text;
        }
        catch (KubeException ex)
        {
            Yaml = ex.Message;
        }
    }

    /// <summary>A shell in the pod's container (bash when it has it, else sh), started the first time the tab shows.</summary>
    [ObservableProperty]
    public partial TerminalSession? Terminal { get; set; }

    void OpenShell()
    {
        if (Terminal is not null || Object.Kind != KubeKind.Pod || _window.Kubectl is not { } kubectl)
            return;
        List<string> args = [.. _window.At(Object), "exec", "-it", Object.Name];
        if ((Container.Name ?? Object.Containers.FirstOrDefault()) is { } container)
            args.AddRange(["-c", container]);
        args.AddRange(["--", "sh", "-c", "if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi"]);
        Terminal = new TerminalSession(kubectl.Program, args) { RestartHint = "Press Enter to connect again." };
    }

    // ---- What is done to it ---------------------------------------------------------------------------------------

    async Task ActAsync(string title, string message, string confirm, Func<Task<string>> action, bool danger = false)
    {
        if (!await _window.ConfirmAsync(title, message, confirm, danger))
            return;
        IsBusy = true;
        try
        {
            var said = await action();
            _window.Host.Status(said.Length > 0 ? said.Split('\n')[0] : $"{title}: done.");
            Error = null;
        }
        catch (KubeException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    Task RestartAsync() => ActAsync($"Restart {Object.Name}?", $"Its pods are replaced one by one (kubectl rollout restart {Object.Ref}).", "Restart",
        () => _window.RestartAsync(Object));

    [RelayCommand]
    async Task ScaleAsync()
    {
        var current = int.TryParse(Object.Ready.Split('/').LastOrDefault(), out var n) ? n : 1;
        if (await _window.ShowAsync(new KubeInputDialog($"Scale {Object.Name}", "How many pods it runs:", current.ToString(), v => int.TryParse(v, out var r) && r >= 0)) is not { } form)
            return;
        var replicas = int.Parse(form.Value);
        await ActAsync($"Scale {Object.Name} to {replicas}?", replicas == 0 ? "It stops: no pod runs until it is scaled up again." : $"{replicas} pods run.", "Scale",
            () => _window.ScaleAsync(Object, replicas), danger: replicas == 0);
    }

    [RelayCommand]
    Task UndoAsync() => ActAsync($"Roll back {Object.Name}?", "It goes back to its revision before this one (kubectl rollout undo).", "Roll Back",
        () => _window.UndoAsync(Object));

    [RelayCommand]
    Task DeleteAsync() => ActAsync($"Delete {Object.Name}?",
        Object.Kind == KubeKind.Pod ? "Its controller makes a new one, when it has one; a pod of none is gone." : "The job and its pods are gone.", "Delete",
        () => _window.DeleteAsync(Object), danger: true);

    [RelayCommand]
    Task TriggerAsync() => ActAsync($"Run {Object.Name} now?", "A job is made from it now, as its schedule would.", "Run Now", () => _window.TriggerAsync(Object));

    [RelayCommand]
    Task SuspendAsync() => ActAsync(IsSuspended ? $"Resume {Object.Name}?" : $"Suspend {Object.Name}?",
        IsSuspended ? "It runs on its schedule again." : "It does not run on its schedule until resumed.", IsSuspended ? "Resume" : "Suspend",
        () => _window.SuspendAsync(Object, !IsSuspended));

    [RelayCommand]
    Task SyncAsync() => ActAsync($"Sync {Object.Name}?",
        IsArgo ? "Argo CD applies what its Git source says now." : "Flux reconciles it with its source now.", "Sync", () => _window.SyncAsync(Object));

    [RelayCommand]
    async Task RefreshAppAsync()
    {
        try
        {
            await _window.RefreshAppAsync(Object);
            _window.Host.Status($"{Object.Name}: Argo CD reads its source again.");
        }
        catch (KubeException ex)
        {
            Error = ex.Message;
        }
        await RefreshAsync();
    }

    /// <summary>A port forwarded to one here: its first port by default, the same number here when it is free.</summary>
    [RelayCommand]
    async Task ForwardAsync()
    {
        var remote = Object.Ports.FirstOrDefault(80);
        var local = remote < 1024 ? 8000 + remote % 1000 : remote;
        if (await _window.ShowAsync(new KubeInputDialog($"Forward a port of {Object.Name}", "Here:there (localhost's port, then its):", $"{local}:{remote}",
                v => v.Split(':') is [var a, var b] && int.TryParse(a, out var x) && int.TryParse(b, out var y) && x is > 0 and < 65536 && y is > 0 and < 65536)) is not { } form)
            return;
        var ports = form.Value.Split(':');
        try
        {
            _window.Forward(Object, int.Parse(ports[0]), int.Parse(ports[1]));
        }
        catch (KubeException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Claude Code with the object's state, its events and its last log lines, ready to send.</summary>
    [RelayCommand]
    void AskClaude()
    {
        var events = string.Join("\n", Events.Where(e => e.IsWarning).Take(8).Select(e => $"- {e.Heading}: {e.Message}"));
        var lines = Logs.Document.Text.Split('\n').Where(l => l.Length > 0).TakeLast(30).ToList();
        _window.Host.AskClaude(
            $"{Object.Kind.Singular()} {Object.Namespace}/{Object.Name} in the Kubernetes context {Context.Name} is {Object.Status}"
            + (Object.Ready.Length > 0 ? $" ({Object.Ready} ready)" : "") + ". "
            + (events.Length > 0 ? $"Its warnings:\n{events}\n" : "")
            + (lines.Count > 0 ? $"Its last log lines:\n```\n{string.Join("\n", lines)}\n```\n" : "")
            + "Find out why with mcp__karvon__k8s_describe, k8s_logs and k8s_events, and tell me what to change. Change nothing in the cluster unless I ask.");
    }

    public void Dispose()
    {
        _disposed = true;
        _logs?.Dispose();
        _logs = null;
        if (Terminal is { } terminal)
            _ = terminal.DisposeAsync().AsTask();
        Terminal = null;
    }
}

/// <summary>A value asked for (replicas, ports), checked as it is typed.</summary>
public sealed partial class KubeInputDialog(string title, string label, string value, Func<string, bool> valid) : Karvon.Ui.OverlayDialog(title, "OK")
{
    public string Label => label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Value { get; set; } = value;

    public override bool CanConfirm => valid(Value.Trim());
}
