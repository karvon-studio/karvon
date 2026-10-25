using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Kubernetes;
using Karvon.Kubernetes.ViewModels;
using Karvon.Kubernetes.Views;
using Karvon.Ui;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A kubectl of the tests' own (TestAssets/FakeKubectl): a cluster from a state file, every call logged.</summary>
sealed class FakeKubectl
{
    public FakeKubectl(JsonObject state)
    {
        Dir = Path.Combine(Path.GetTempPath(), "karvon-fake-kubectl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        var script = Path.Combine(Dir, "kubectl.py");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeKubectl", "kubectl.py"), script);
        Command = TestTools.Command(script, Dir, new Dictionary<string, string> { ["FAKE_KUBECTL_DATA"] = Dir });
        File.WriteAllText(Path.Combine(Dir, "state.json"), state.ToJsonString());
    }

    public string Dir { get; }

    public string Command { get; }

    public IReadOnlyList<string> Calls => File.Exists(Path.Combine(Dir, "calls.txt")) ? File.ReadAllLines(Path.Combine(Dir, "calls.txt")) : [];
}

sealed class TestKubernetesHost : IKubernetesHost
{
    public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "karvon-k8s-config-" + Guid.NewGuid().ToString("N"));

    public List<DocumentTab> Open { get; } = [];

    public IEnumerable<DocumentTab> Documents => Open;

    public void ShowDocument(DocumentTab document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
    }

    public List<string> Urls { get; } = [];

    public List<string> Statuses { get; } = [];

    public List<string> Prompts { get; } = [];

    public void OpenUrl(string url) => Urls.Add(url);

    public void Status(string text) => Statuses.Add(text);

    public void AskClaude(string prompt) => Prompts.Add(prompt);
}

/// <summary>Kubernetes: kubectl's JSON read, the window and an object's page, Claude's tools; through a fake kubectl.</summary>
public class KubernetesTests
{
    static string At(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

    static object Meta(string name, string ns, int minutesAgo = 60 * 24, object? labels = null) =>
        new { name, @namespace = ns, creationTimestamp = At(minutesAgo), labels = labels ?? new { } };

    static object Pod(string name, string ns, bool ready, string? waiting = null, int restarts = 0) => new
    {
        apiVersion = "v1", kind = "Pod", metadata = Meta(name, ns, labels: new { app = "web" }),
        spec = new { nodeName = "node-1", containers = new[] { new { name = "web", image = "registry.example/shop/web:1.4", ports = new[] { new { containerPort = 8080 } } } } },
        status = new
        {
            phase = "Running", podIP = "10.1.0.7",
            containerStatuses = new[]
            {
                new { name = "web", ready, restartCount = restarts, state = waiting is null ? (object)new { running = new { startedAt = At(30) } } : new { waiting = new { reason = waiting } } },
            },
        },
    };

    /// <summary>A shop's namespace (a web deployment half up, its two pods, one crashing, its service, its config and secret, a cron job), an Argo CD app, another namespace's pod.</summary>
    static JsonObject Cluster() => JsonSerializer.SerializeToNode(new
    {
        config = new
        {
            current_context = "dev",
            contexts = new object[]
            {
                new { name = "dev", context = new { cluster = "dev-cluster", user = "dev-admin", @namespace = "shop" } },
                new { name = "prod-eu", context = new { cluster = "prod-eu-cluster", user = "ops" } },
            },
        },
        namespaces = new[] { "default", "shop" },
        api_resources = new[] { "pods", "deployments.apps", "applications.argoproj.io" },
        objects = new object[]
        {
            new
            {
                apiVersion = "apps/v1", kind = "Deployment", metadata = Meta("web", "shop"),
                spec = new
                {
                    replicas = 2, selector = new { matchLabels = new { app = "web" } }, strategy = new { type = "RollingUpdate" },
                    template = new { spec = new { containers = new[] { new { name = "web", image = "registry.example/shop/web:1.4", ports = new[] { new { containerPort = 8080 } } } } } },
                },
                status = new { readyReplicas = 1, updatedReplicas = 2, conditions = new[] { new { type = "Available", status = "False", reason = "MinimumReplicasUnavailable", message = "Deployment does not have minimum availability." } } },
            },
            Pod("web-abc", "shop", ready: true), Pod("web-def", "shop", ready: false, waiting: "CrashLoopBackOff", restarts: 5),
            Pod("other", "default", ready: true),
            new
            {
                apiVersion = "v1", kind = "Service", metadata = Meta("web", "shop"),
                spec = new { type = "ClusterIP", clusterIP = "10.0.0.5", selector = new { app = "web" }, ports = new[] { new { name = "http", port = 80, protocol = "TCP", targetPort = 8080 } } },
                status = new { },
            },
            new { apiVersion = "v1", kind = "ConfigMap", metadata = Meta("web-config", "shop"), data = new { LOG_LEVEL = "info" } },
            new { apiVersion = "v1", kind = "Secret", metadata = Meta("web-secrets", "shop"), type = "Opaque", data = new { DB_PASSWORD = Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2")) } },
            new { apiVersion = "batch/v1", kind = "CronJob", metadata = Meta("nightly", "shop"), spec = new { schedule = "0 2 * * *", suspend = false }, status = new { lastScheduleTime = At(600) } },
            new
            {
                apiVersion = "argoproj.io/v1alpha1", kind = "Application", metadata = Meta("shop", "argocd"),
                spec = new { source = new { repoURL = "https://git.example/ops/deploy.git", path = "shop", targetRevision = "main" }, destination = new { @namespace = "shop" } },
                status = new { sync = new { status = "OutOfSync", revision = "abc123" }, health = new { status = "Healthy" } },
            },
        },
        events = new object[]
        {
            new
            {
                metadata = new { name = "e1", @namespace = "shop", creationTimestamp = At(3) }, type = "Warning", reason = "BackOff", count = 12,
                message = "Back-off restarting failed container web", lastTimestamp = At(1), involvedObject = new { kind = "Pod", name = "web-def" },
            },
            new
            {
                metadata = new { name = "e2", @namespace = "shop", creationTimestamp = At(20) }, type = "Normal", reason = "ScalingReplicaSet", count = 1,
                message = "Scaled up replica set web-7d9 to 2", lastTimestamp = At(20), involvedObject = new { kind = "Deployment", name = "web" },
            },
        },
        logs = new Dictionary<string, string[]> { ["web-abc"] = ["listening on :8080"], ["web-def"] = ["panic: no DB_PASSWORD"] },
        describe = "Name:         web-secrets\nType:  Opaque\nData\n====\nDB_PASSWORD:  7 bytes",
    })!.AsObject().ReplaceKey("current_context", "current-context");

    [Fact]
    public void Kubectls_objects_say_how_they_stand_and_never_a_secrets_value()
    {
        var cluster = Cluster();
        var objects = KubeParse.List(JsonSerializer.SerializeToElement(new { items = cluster["objects"] }));
        KubeObject Of(KubeKind kind, string name) => objects.Single(o => o.Kind == kind && o.Name == name);

        var web = Of(KubeKind.Deployment, "web");
        Assert.Equal(("Updating", "1/2", "web:1.4", true, "app=web"), (web.Status, web.Ready, web.Detail, web.IsProblem, web.Selector));
        Assert.Equal([8080], web.Ports);
        var crashing = Of(KubeKind.Pod, "web-def");
        Assert.Equal(("CrashLoopBackOff", "0/1", "5 restarts · node-1 · 10.1.0.7", true), (crashing.Status, crashing.Ready, crashing.Detail, crashing.IsProblem));
        Assert.Equal(("Running", false), (Of(KubeKind.Pod, "web-abc").Status, Of(KubeKind.Pod, "web-abc").IsProblem));
        Assert.Equal(("ClusterIP", "10.0.0.5 · 80/TCP→8080"), (Of(KubeKind.Service, "web").Status, Of(KubeKind.Service, "web").Detail));
        Assert.Equal("1 key", Of(KubeKind.ConfigMap, "web-config").Detail);
        var secret = Of(KubeKind.Secret, "web-secrets");
        Assert.Equal("Opaque · 1 key", secret.Detail);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2")), secret.Json.GetRawText());
        Assert.Equal("Scheduled", Of(KubeKind.CronJob, "nightly").Status);
        var app = Of(KubeKind.ArgoApplication, "shop");
        Assert.Equal(("OutOfSync · Healthy", "deploy.git shop @main", true), (app.Status, app.Detail, app.IsProblem));

        var contexts = KubeParse.Contexts(JsonSerializer.SerializeToElement(cluster["config"]));
        Assert.Equal([("dev", true, false), ("prod-eu", false, true)], contexts.Select(c => (c.Name, c.IsCurrent, c.IsProduction)));
        Assert.Equal(KubeKind.Deployment, KubeKinds.Parse("deploy"));
    }

    [AvaloniaFact]
    public async Task The_window_shows_a_namespace_and_an_object_opens_as_its_page()
    {
        var interval = KubernetesViewModel.Interval;
        KubernetesViewModel.Interval = TimeSpan.FromMinutes(5);
        try
        {
            var kubectl = new FakeKubectl(Cluster());
            var host = new TestKubernetesHost();
            using var k8s = new KubernetesViewModel(host, new Kubectl(kubectl.Command));
            var window = new Window { Width = 420, Height = 760, Content = new KubernetesView { DataContext = k8s } };
            window.Show();

            await WaitUntil(() => k8s.Groups.Any(g => g.Kind == KubeKind.Pod && g.Items.Count == 2), TimeSpan.FromSeconds(15));
            Assert.Equal(("dev", "shop"), (k8s.Context?.Name, k8s.NamespaceName));
            Assert.Equal(["All Namespaces", "default", "shop"], k8s.Namespaces.Select(n => n.ToString()));
            var pods = k8s.Groups.Single(g => g.Kind == KubeKind.Pod);
            // The failing first.
            Assert.Equal(["web-def", "web-abc"], pods.Items.Select(r => r.Name));
            Assert.Equal(1, pods.Problems);
            Assert.Contains(k8s.Groups, g => g.Kind == KubeKind.ArgoApplication && g.Items.Single().Detail.StartsWith("argocd · "));
            // kubectl's own context is never switched: every call names its context.
            Assert.All(kubectl.Calls.Where(c => !c.StartsWith("config view")), c => Assert.StartsWith("--context dev", c));
            Snapshot(window, "kubernetes");

            k8s.Filter = "def";
            Assert.Equal(["web-def"], pods.Items.Select(r => r.Name));
            k8s.Filter = "";

            // The deployment's page: its facts, its pods (by its selector), its events; the logs of its pods.
            var page = k8s.Open(k8s.Groups.Single(g => g.Kind == KubeKind.Deployment).Items.Single().Object)!.Page;
            await WaitUntil(() => page.Pods.Count == 2 && page.HasEvents, TimeSpan.FromSeconds(10));
            Assert.Equal(("Deployment web", "Updating", "1/2 ready"), (page.Heading, page.Status, page.Ready));
            Assert.Contains(new KubeFact("Strategy", "RollingUpdate"), page.Info);
            Assert.Equal("ScalingReplicaSet", page.Events.Single().Heading);
            Assert.Equal("MinimumReplicasUnavailable · Deployment does not have minimum availability.", page.Conditions.Single().Text);
            var pageWindow = new Window { Width = 1000, Height = 720, Content = new KubePageView { DataContext = page } };
            pageWindow.Show();
            Snapshot(pageWindow, "kubernetes-page");
            page.Tab = "logs";
            await WaitUntil(() => page.Logs.Document.Text.Contains("panic: no DB_PASSWORD"), TimeSpan.FromSeconds(10));
            Assert.Contains("[pod/web-abc/web] listening on :8080", page.Logs.Document.Text);
            page.Tab = "yaml";
            await WaitUntil(() => page.Yaml.StartsWith("kind: Deployment"), TimeSpan.FromSeconds(10));

            // A restart and a scale, each asked first; a port forwarded and stopped.
            var restarting = page.RestartCommand.ExecuteAsync(null);
            await WaitUntil(() => k8s.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
            k8s.Dialog!.Confirm();
            await restarting;
            Assert.Contains("--context dev --namespace shop rollout restart deployments.apps/web", kubectl.Calls);
            var scaling = page.ScaleCommand.ExecuteAsync(null);
            await WaitUntil(() => k8s.Dialog is KubeInputDialog, TimeSpan.FromSeconds(5));
            var replicas = (KubeInputDialog)k8s.Dialog!;
            Assert.Equal("2", replicas.Value);
            replicas.Value = "x";
            Assert.False(replicas.CanConfirm);
            replicas.Value = "3";
            replicas.Confirm();
            await WaitUntil(() => k8s.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
            k8s.Dialog!.Confirm();
            await scaling;
            Assert.Contains("--context dev --namespace shop scale deployments.apps/web --replicas=3", kubectl.Calls);
            var forwarding = page.ForwardCommand.ExecuteAsync(null);
            await WaitUntil(() => k8s.Dialog is KubeInputDialog, TimeSpan.FromSeconds(5));
            Assert.Equal("8080:8080", ((KubeInputDialog)k8s.Dialog!).Value);
            k8s.Dialog!.Confirm();
            await forwarding;
            var forward = Assert.Single(k8s.Forwards);
            await WaitUntil(() => forward.IsForwarding, TimeSpan.FromSeconds(10));
            Assert.Equal("http://localhost:8080", forward.Url);
            k8s.StopForwardCommand.Execute(forward);
            Assert.Empty(k8s.Forwards);

            // A secret's page: its keys only.
            var secretPage = k8s.Open(k8s.Groups.Single(g => g.Kind == KubeKind.Secret).Items.Single().Object)!.Page;
            Assert.Contains(new KubeFact("DB_PASSWORD", "(hidden)"), secretPage.Info);
            secretPage.Tab = "yaml";
            await WaitUntil(() => secretPage.Yaml.Contains("7 bytes"), TimeSpan.FromSeconds(10));
            Assert.DoesNotContain("hunter2", secretPage.Yaml);

            // Every namespace: the other's pod too, said whose.
            k8s.Namespace = k8s.Namespaces[0];
            await WaitUntil(() => pods.Items.Count == 3, TimeSpan.FromSeconds(10));
            Assert.Contains(pods.Items, r => r.Name == "other" && r.Detail.StartsWith("default · "));

            // A production context: its changes ask in red, saying so.
            k8s.Context = k8s.Contexts.Single(c => c.Name == "prod-eu");
            await WaitUntil(() => k8s.IsProduction && k8s.NamespaceName == "default", TimeSpan.FromSeconds(10));
            var asking = k8s.ConfirmAsync("Restart web?", "Its pods are replaced.", "Restart");
            var red = Assert.IsType<ConfirmDialog>(k8s.Dialog);
            Assert.True(red.IsDanger);
            Assert.Contains("prod-eu is a production cluster.", red.Message);
            red.Cancel();
            Assert.False(await asking);
            pageWindow.Close();
            window.Close();
            // Their logs' kubectl ends with them.
            foreach (var document in host.Open)
                document.Dispose();

            // The context and namespace chosen last come back.
            using var again = new KubernetesViewModel(host, new Kubectl(kubectl.Command));
            await WaitUntil(() => again.Context?.Name == "prod-eu" && again.NamespaceName == "default", TimeSpan.FromSeconds(10));
        }
        finally
        {
            KubernetesViewModel.Interval = interval;
        }
    }

    [AvaloniaFact]
    public async Task Without_kubectl_the_window_says_what_it_needs()
    {
        using var k8s = new KubernetesViewModel(new TestKubernetesHost(), null);
        await WaitUntil(() => k8s.Message is not null, TimeSpan.FromSeconds(5));
        Assert.StartsWith("kubectl was not found.", k8s.Message);
        var tools = KarvonTools.Create(KubernetesClaudeTools.Create(() => k8s));
        Assert.Equal("kubectl was not found on this machine.",
            (await tools.Tools.Single(t => t.Name == "k8s_get").CallAsync(JsonSerializer.SerializeToElement(new { }), default)).Text);
    }

    [AvaloniaFact]
    public async Task Claude_reads_the_cluster_and_asks_before_it_changes_it()
    {
        var kubectl = new FakeKubectl(Cluster());
        using var k8s = new KubernetesViewModel(new TestKubernetesHost(), new Kubectl(kubectl.Command));
        await WaitUntil(() => k8s.NamespaceName == "shop", TimeSpan.FromSeconds(15));
        var tools = KarvonTools.Create(KubernetesClaudeTools.Create(() => k8s));
        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        async Task<ClaudeToolOutput> Call(string name, object input) => await tools.Tools.Single(t => t.Name == name).CallAsync(Input(input), default);

        Assert.Contains("dev: cluster dev-cluster, user dev-admin, namespace shop (kubectl's current) (Karvon's, namespace shop)", (await Call("k8s_contexts", new { })).Text);
        var get = (await Call("k8s_get", new { })).Text;
        Assert.StartsWith("dev, namespace shop:\n", get);
        Assert.Contains("pod/web-def (shop) [CrashLoopBackOff] 0/1 ready", get);
        Assert.Contains("(problem)", get);
        Assert.DoesNotContain("hunter2", (await Call("k8s_get", new { kind = "secret", name = "web-secrets" })).Text);
        Assert.Equal("panic: no DB_PASSWORD", (await Call("k8s_logs", new { name = "web-def" })).Text);
        Assert.Contains("Warning BackOff Pod/web-def ×12: Back-off restarting failed container web", (await Call("k8s_events", new { })).Text);
        Assert.True((await Call("k8s_get", new { context = "staging" })).IsError);

        var restart = tools.Plan(ClaudeToolServer.Prefix + "k8s_restart", Input(new { name = "web" }))!;
        Assert.Equal((ToolAccess.Write, "Allow Claude to restart deployment web in dev?"), (restart.Access, restart.Question));
        Assert.Equal(ToolAccess.Destructive, tools.Plan(ClaudeToolServer.Prefix + "k8s_restart", Input(new { name = "web", context = "prod-eu" }))!.Access);
        Assert.Equal(ToolAccess.Destructive, tools.Plan(ClaudeToolServer.Prefix + "k8s_scale", Input(new { name = "web", replicas = 0 }))!.Access);
        Assert.Equal(ToolAccess.Destructive, tools.Plan(ClaudeToolServer.Prefix + "k8s_delete_pod", Input(new { name = "web-def" }))!.Access);
        Assert.Equal(ToolAccess.Read, tools.Plan(ClaudeToolServer.Prefix + "k8s_logs", Input(new { name = "web-def" }))!.Access);
        Assert.Equal("deployments.apps/web restarted", (await Call("k8s_restart", new { name = "web" })).Text);
        Assert.True((await Call("k8s_restart", new { kind = "pod", name = "web-def" })).IsError);
        await Call("k8s_delete_pod", new { name = "web-def" });
        Assert.Contains("--context dev --namespace shop delete pod web-def --wait=false", kubectl.Calls);
    }

    [Theory]
    [InlineData("kubectl get pods", true)]
    [InlineData("kubectl -n shop logs web-def --previous", true)]
    [InlineData("kubectl --context=prod describe deploy web", true)]
    [InlineData("kubectl rollout restart deploy/web", true)]
    [InlineData("kubectl delete pod web-def", true)]
    [InlineData("kubectl apply -f deploy.yaml", false)]
    [InlineData("kubectl delete deployment web", false)]
    [InlineData("kubectl exec -it web-abc -- sh", false)]
    public void Kubectls_reading_goes_to_the_Kubernetes_tools(string command, bool redirected) =>
        Assert.Equal(redirected, new KubernetesModule().ClaudeShellRules().Any(r => r.Matches(command)));
}

static class JsonNodeKeys
{
    /// <summary>A key renamed (C#'s names have no hyphen: kubeconfig's current-context).</summary>
    public static JsonObject ReplaceKey(this JsonObject root, string from, string to)
    {
        var config = root["config"]!.AsObject();
        var value = config[from];
        config.Remove(from);
        config[to] = value;
        return root;
    }
}
