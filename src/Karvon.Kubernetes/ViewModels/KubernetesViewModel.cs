using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;
using Karvon.ViewModels;

namespace Karvon.Kubernetes.ViewModels;

/// <summary>What the Kubernetes window needs of Karvon.</summary>
public interface IKubernetesHost
{
    /// <summary>Where the window keeps the context and namespace last chosen.</summary>
    string ConfigDirectory { get; }

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void OpenUrl(string url);

    void Status(string text);

    void AskClaude(string prompt);
}

/// <summary>An object in the window's tree, updated in place as the cluster changes.</summary>
public sealed partial class KubeRow(KubeObject o, bool showNamespace) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(Ready), nameof(Detail), nameof(IsProblem), nameof(HasStatus), nameof(Age))]
    public partial KubeObject Object { get; set; } = o;

    public string Name => Object.Name;

    public string Icon => Object.Kind.Icon();

    public string Status => Object.Status;

    public bool HasStatus => Object.Status.Length > 0;

    public string Ready => Object.Ready;

    public string Age => KubeParse.Age(Object.Created);

    public string Detail => showNamespace && Object.Namespace is { } ns ? $"{ns} · {Object.Detail}".TrimEnd(' ', '·') : Object.Detail;

    public bool IsProblem => Object.IsProblem;
}

/// <summary>A kind's objects in the tree (Deployments, Pods, Argo CD Applications), with how many and how many are failing.</summary>
public sealed partial class KubeGroup(KubeKind kind) : ObservableObject
{
    public KubeKind Kind => kind;

    public string Title => kind.Plural();

    public string Icon => kind.Icon();

    public ObservableCollection<KubeRow> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Count => Items.Count.ToString();

    public int Problems => Items.Count(i => i.IsProblem);

    public bool HasProblems => Problems > 0;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
    }
}

/// <summary>A port forward: kubectl port-forward of a pod's, a service's or a workload's port to one here.</summary>
public sealed partial class PortForwardRow : ObservableObject, IDisposable
{
    internal PortForwardRow(string context, string? ns, KubeObject target, int local, int remote)
    {
        Context = context;
        Namespace = ns;
        Target = target;
        Local = local;
        Remote = remote;
    }

    public string Context { get; }

    public string? Namespace { get; }

    public KubeObject Target { get; }

    public int Local { get; }

    public int Remote { get; }

    public string Title => $"{Target.Kind.Singular().ToLowerInvariant()}/{Target.Name} :{Remote}";

    public string Url => $"http://localhost:{Local}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForwarding))]
    public partial string State { get; set; } = "Starting…";

    public bool IsForwarding => State == "Forwarding";

    internal KubeStream? Stream { get; set; }

    public void Dispose() => Stream?.Dispose();
}

/// <summary>A choice of the window's namespace box: one namespace, or every one (null).</summary>
public sealed record NamespaceChoice(string? Name)
{
    public override string ToString() => Name ?? "All Namespaces";
}

/// <summary>
/// The Kubernetes window: kubeconfig's contexts (through the user's own kubectl, never switching its current context), a
/// context's namespaces, and in them the workloads (deployments, stateful and daemon sets, jobs, cron jobs), pods,
/// services, ingresses, config maps and secrets (their keys only), read again every ten seconds; the cluster's GitOps
/// apps (Argo CD's, Flux's) when it has them. An object opens as a page; the port forwards it made are listed here.
/// </summary>
public sealed partial class KubernetesViewModel : ObservableObject, IDisposable
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<string, HashSet<KubeKind>> _gitOps = [];
    DateTimeOffset _next = DateTimeOffset.MaxValue;
    bool _reading;
    bool _loading;

    public KubernetesViewModel(IKubernetesHost host, Kubectl? kubectl)
    {
        Host = host;
        Kubectl = kubectl;
        foreach (var kind in KubeKinds.Namespaced)
            Groups.Add(new KubeGroup(kind) { IsExpanded = kind is KubeKind.Deployment or KubeKind.Pod });
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = LoadAsync();
    }

    public IKubernetesHost Host { get; }

    public Kubectl? Kubectl { get; }

    public ObservableCollection<KubeContext> Contexts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContext), nameof(IsProduction))]
    public partial KubeContext? Context { get; set; }

    public bool HasContext => Context is not null;

    /// <summary>The context is a production cluster's: its changes ask in red.</summary>
    public bool IsProduction => Context?.IsProduction == true;

    public ObservableCollection<NamespaceChoice> Namespaces { get; } = [];

    [ObservableProperty]
    public partial NamespaceChoice? Namespace { get; set; }

    /// <summary>The namespace chosen; null for every one.</summary>
    public string? NamespaceName => Namespace?.Name;

    public ObservableCollection<KubeGroup> Groups { get; } = [];

    public ObservableCollection<PortForwardRow> Forwards { get; } = [];

    public bool HasForwards => Forwards.Count > 0;

    /// <summary>Only the objects whose name has it.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Show(_last);

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial string? Warning { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    IReadOnlyList<KubeObject> _last = [];

    public async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    /// <summary>A change asked first; red on a production cluster or when it cannot be undone.</summary>
    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        await ShowAsync(new ConfirmDialog(title, IsProduction ? $"{message}\n\n{Context!.Name} is a production cluster." : message, confirm, danger || IsProduction)) is not null;

    string SettingsFile => Path.Combine(Host.ConfigDirectory, "kubernetes.json");

    (string? Context, string? Namespace) Saved()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(SettingsFile));
            return (json?["context"]?.GetValue<string>(), json?["namespace"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    void Save()
    {
        if (_loading || Context is null)
            return;
        try
        {
            Directory.CreateDirectory(Host.ConfigDirectory);
            File.WriteAllText(SettingsFile, new JsonObject { ["context"] = Context.Name, ["namespace"] = NamespaceName ?? "*" }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>kubeconfig's contexts, and the one chosen last (else kubectl's current one).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Kubectl is null)
        {
            Message = "kubectl was not found. Install it, with a kubeconfig (~/.kube/config) for your clusters: Karvon uses it as you do.";
            return;
        }
        IsBusy = true;
        try
        {
            var (config, _) = await Kubectl.JsonAsync(["config", "view"]);
            using (config)
            {
                var contexts = KubeParse.Contexts(config.RootElement);
                _loading = true;
                Contexts.Clear();
                foreach (var c in contexts)
                    Contexts.Add(c);
                var (saved, _) = Saved();
                _loading = false;
                if (Contexts.Count == 0)
                {
                    Message = "kubeconfig has no contexts: add one for a cluster (kubectl config set-context, or your cloud's CLI).";
                    return;
                }
                Message = null;
                Context = Contexts.FirstOrDefault(c => c.Name == saved) ?? Contexts.FirstOrDefault(c => c.IsCurrent) ?? Contexts[0];
            }
        }
        catch (KubeException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    partial void OnContextChanged(KubeContext? value)
    {
        if (value is not null)
            _ = ContextChangedAsync(value);
    }

    async Task ContextChangedAsync(KubeContext context)
    {
        foreach (var group in Groups)
            group.Items.Clear();
        _last = [];
        var (savedContext, savedNamespace) = Saved();
        IReadOnlyList<string> names;
        try
        {
            var (json, _) = await Kubectl!.JsonAsync([.. Kubectl.In(context.Name, null), "get", "namespaces", "--request-timeout=15s"]);
            using (json)
                names = [.. KubeParse.Array(json.RootElement, "items").Select(n => KubeParse.Str(KubeParse.Prop(n, "metadata"), "name")).Order()];
        }
        catch (KubeException ex)
        {
            // One who may not list them still has their context's.
            names = [context.Namespace ?? "default"];
            Warning = ex.Message;
        }
        if (Context != context)
            return;
        _loading = true;
        Namespaces.Clear();
        Namespaces.Add(new NamespaceChoice(null));
        foreach (var name in names)
            Namespaces.Add(new NamespaceChoice(name));
        var wanted = savedContext == context.Name && savedNamespace is { } kept ? kept == "*" ? null : kept : context.Namespace ?? "default";
        Namespace = Namespaces.FirstOrDefault(n => n.Name == wanted) ?? Namespaces.FirstOrDefault(n => n.Name == "default") ?? Namespaces[0];
        _loading = false;
        Save();
        await RefreshAsync();
    }

    partial void OnNamespaceChanged(NamespaceChoice? value)
    {
        if (_loading)
            return;
        Save();
        foreach (var group in Groups)
            group.Items.Clear();
        _last = [];
        _ = RefreshAsync();
    }

    void Tick()
    {
        if (DateTimeOffset.Now >= _next && !_reading)
            _ = RefreshAsync();
    }

    /// <summary>The namespace's objects (and the cluster's GitOps apps) read again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Kubectl is null || Context is not { } context || Namespace is null || _reading)
            return;
        _reading = true;
        IsBusy = true;
        var ns = NamespaceName;
        try
        {
            string[] scope = ns is null ? [.. Kubectl.In(context.Name, null), "get", "--all-namespaces"] : [.. Kubectl.In(context.Name, ns), "get"];
            var (json, warning) = await Kubectl.JsonAsync([.. scope, string.Join(',', KubeKinds.Namespaced.Select(k => k.Resource())), "--request-timeout=20s"]);
            List<KubeObject> objects;
            using (json)
                objects = [.. KubeParse.List(json.RootElement)];
            var gitOps = await GitOpsKindsAsync(context);
            if (gitOps.Count > 0)
            {
                try
                {
                    var (apps, _) = await Kubectl.JsonAsync([.. Kubectl.In(context.Name, null), "get", "--all-namespaces",
                        string.Join(',', gitOps.Select(k => k.Resource())), "--request-timeout=20s"]);
                    using (apps)
                        objects.AddRange(KubeParse.List(apps.RootElement));
                }
                catch (KubeException ex)
                {
                    warning ??= ex.Message;
                }
            }
            if (Context != context || NamespaceName != ns)
                return;
            _last = objects;
            Show(objects);
            Warning = warning;
            Message = null;
        }
        catch (KubeException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            _reading = false;
            IsBusy = false;
            _next = DateTimeOffset.Now + Interval;
        }
    }

    /// <summary>The GitOps kinds the cluster has (Argo CD's, Flux's), asked once a context.</summary>
    async Task<IReadOnlyCollection<KubeKind>> GitOpsKindsAsync(KubeContext context)
    {
        if (_gitOps.TryGetValue(context.Name, out var known))
            return known;
        var found = new HashSet<KubeKind>();
        var result = await Kubectl!.RunAsync([.. Kubectl.In(context.Name, null), "api-resources", "-o", "name", "--request-timeout=15s"]);
        if (result.Succeeded)
        {
            var names = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            foreach (var kind in KubeKinds.GitOps)
                if (names.Contains(kind.Resource()))
                    found.Add(kind);
        }
        _gitOps[context.Name] = found;
        return found;
    }

    void Show(IReadOnlyList<KubeObject> objects)
    {
        var filter = Filter.Trim();
        var all = NamespaceName is null;
        foreach (var kind in KubeKinds.GitOps)
            if (objects.Any(o => o.Kind == kind) && Groups.All(g => g.Kind != kind))
                Groups.Add(new KubeGroup(kind) { IsExpanded = true });
        foreach (var group in Groups)
        {
            var wanted = objects.Where(o => o.Kind == group.Kind && (filter.Length == 0 || o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(o => o.IsProblem).ThenBy(o => o.Namespace).ThenBy(o => o.Name).ToList();
            var rows = group.Items.ToDictionary(r => r.Object.Key);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (rows.Remove(wanted[i].Key, out var row))
                {
                    row.Object = wanted[i];
                    var at = group.Items.IndexOf(row);
                    if (at != i)
                        group.Items.Move(at, i);
                }
                else
                    group.Items.Insert(i, new KubeRow(wanted[i], all || group.Kind is KubeKind.ArgoApplication or KubeKind.FluxKustomization or KubeKind.FluxHelmRelease));
            }
            foreach (var gone in rows.Values)
                group.Items.Remove(gone);
            group.Changed();
        }
        foreach (var page in Host.Documents.OfType<KubeDocument>())
            if (objects.FirstOrDefault(o => o.Key == page.Page.Object.Key) is { } fresh && page.Page.Context == Context)
                page.Page.Object = fresh;
    }

    /// <summary>All the objects last read (Claude's tools look in them first).</summary>
    public IReadOnlyList<KubeObject> Objects => _last;

    /// <summary>An object's page: the one open, else a new one.</summary>
    [RelayCommand]
    public void Open(KubeRow? row)
    {
        if (row is not null)
            Open(row.Object);
    }

    public KubeDocument? Open(KubeObject o)
    {
        if (Context is not { } context)
            return null;
        var document = Host.Documents.OfType<KubeDocument>().FirstOrDefault(d => d.Page.Context == context && d.Page.Object.Key == o.Key)
            ?? new KubeDocument(new KubePageViewModel(this, context, o));
        Host.ShowDocument(document);
        _ = document.Page.RefreshAsync();
        return document;
    }

    /// <summary>One object read now (Claude's call, a page's refresh).</summary>
    public async Task<KubeObject> GetAsync(KubeKind kind, string name, string? ns, CancellationToken ct = default)
    {
        var context = ContextOrThrow();
        var (json, _) = await Kubectl!.JsonAsync([.. Kubectl.In(context.Name, ns ?? NamespaceName ?? context.Namespace ?? "default"), "get", kind.Resource(), name], ct);
        using (json)
            return KubeParse.Object(json.RootElement) ?? throw new KubeException($"{kind.Singular()} {name} could not be read.");
    }

    internal KubeContext ContextOrThrow() =>
        Kubectl is null ? throw new KubeException("kubectl was not found on this machine.")
        : Context ?? throw new KubeException(Message ?? "No Kubernetes context is chosen in View > Kubernetes.");

    /// <summary>kubectl in the object's context and namespace.</summary>
    internal IEnumerable<string> At(KubeObject o) => Kubectl.In(ContextOrThrow().Name, o.Namespace);

    // ---- What is done to an object (each asked first by its caller) --------------------------------------------

    internal async Task<string> RunAsync(KubeObject o, IEnumerable<string> command, CancellationToken ct = default)
    {
        var result = await Kubectl!.RunAsync([.. At(o), .. command], cancellationToken: ct);
        if (!result.Succeeded)
            throw new KubeException(Kubectl.Explain(result.Message));
        _next = DateTimeOffset.Now + TimeSpan.FromSeconds(1);
        return result.Output.Trim();
    }

    public Task<string> RestartAsync(KubeObject o, CancellationToken ct = default) => RunAsync(o, ["rollout", "restart", o.Ref], ct);

    public Task<string> ScaleAsync(KubeObject o, int replicas, CancellationToken ct = default) => RunAsync(o, ["scale", o.Ref, $"--replicas={replicas}"], ct);

    public Task<string> DeleteAsync(KubeObject o, CancellationToken ct = default) => RunAsync(o, ["delete", o.Ref, "--wait=false"], ct);

    public Task<string> UndoAsync(KubeObject o, CancellationToken ct = default) => RunAsync(o, ["rollout", "undo", o.Ref], ct);

    /// <summary>A cron job's job made now (kubectl create job --from).</summary>
    public Task<string> TriggerAsync(KubeObject cronJob, CancellationToken ct = default)
    {
        var name = $"{cronJob.Name}-manual-{DateTimeOffset.UtcNow:MMddHHmmss}";
        return RunAsync(cronJob, ["create", "job", name, $"--from=cronjob/{cronJob.Name}"], ct);
    }

    public Task<string> SuspendAsync(KubeObject cronJob, bool suspend, CancellationToken ct = default) =>
        RunAsync(cronJob, ["patch", cronJob.Ref, "--type", "merge", "-p", $"{{\"spec\":{{\"suspend\":{(suspend ? "true" : "false")}}}}}"], ct);

    /// <summary>An Argo CD application synced now (as its Sync button: an operation on it), or a Flux object reconciled.</summary>
    public Task<string> SyncAsync(KubeObject o, CancellationToken ct = default) => o.Kind == KubeKind.ArgoApplication
        ? RunAsync(o, ["patch", o.Ref, "--type", "merge", "-p", """{"operation":{"initiatedBy":{"username":"karvon"},"sync":{}}}"""], ct)
        : RunAsync(o, ["annotate", "--overwrite", o.Ref, $"reconcile.fluxcd.io/requestedAt={DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}"], ct);

    /// <summary>An Argo CD application's state read again from Git (its Refresh).</summary>
    public Task<string> RefreshAppAsync(KubeObject app, CancellationToken ct = default) =>
        RunAsync(app, ["annotate", "--overwrite", app.Ref, "argocd.argoproj.io/refresh=normal"], ct);

    /// <summary>A port of the object's forwarded to one here, kept until stopped (listed in the window).</summary>
    public PortForwardRow Forward(KubeObject o, int local, int remote)
    {
        var context = ContextOrThrow();
        var row = new PortForwardRow(context.Name, o.Namespace, o, local, remote);
        void Update(Action change) => Dispatcher.UIThread.Post(change);
        row.Stream = Kubectl!.Stream([.. At(o), "port-forward", "--address", "127.0.0.1", o.Ref, $"{local}:{remote}"], line =>
        {
            if (line.StartsWith("Forwarding from", StringComparison.Ordinal))
                Update(() => row.State = "Forwarding");
            else if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Unable", StringComparison.Ordinal))
                Update(() => row.State = Kubectl.Explain(line));
        }, code => Update(() =>
        {
            if (row.IsForwarding || row.State.StartsWith("Starting", StringComparison.Ordinal))
                row.State = code == 0 ? "Stopped" : $"Stopped ({code})";
        }));
        Forwards.Add(row);
        OnPropertyChanged(nameof(HasForwards));
        Host.Status($"Forwarding {row.Title} to {row.Url}");
        return row;
    }

    [RelayCommand]
    void StopForward(PortForwardRow? row)
    {
        if (row is null)
            return;
        row.Dispose();
        Forwards.Remove(row);
        OnPropertyChanged(nameof(HasForwards));
    }

    [RelayCommand]
    void OpenForward(PortForwardRow? row)
    {
        if (row is not null)
            Host.OpenUrl(row.Url);
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var forward in Forwards)
            forward.Dispose();
        Forwards.Clear();
    }
}

/// <summary>An object's page in the document well.</summary>
public sealed class KubeDocument(KubePageViewModel page) : DocumentTab
{
    public KubePageViewModel Page => page;

    public override string Key => $"kubernetes:{page.Context.Name}/{page.Object.Key}";

    public override string Title => page.Object.Name;

    public override string ToolTip => $"{page.Object.Kind.Singular()} {page.Object.Namespace}/{page.Object.Name} ({page.Context.Name})";

    public override void Dispose() => page.Dispose();
}
