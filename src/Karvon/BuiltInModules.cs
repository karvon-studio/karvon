using Karvon.Modules;

namespace Karvon;

/// <summary>The modules Karvon Studio comes with, in the order they show on the activity bar and in the View menu.</summary>
public static class BuiltInModules
{
    public static IReadOnlyList<KarvonModule> Create() =>
    [
        new Database.DatabaseModule(), new Docker.DockerModule(), new Servers.ServersModule(), new Jira.JiraModule(), new Ports.PortsModule(), new Pipelines.PipelinesModule(), new PullRequests.PullRequestsModule(), new Kubernetes.KubernetesModule(), new Http.HttpModule(), new Mcp.McpModule(), new Skills.SkillsModule(), new Completions.CompletionsModule(),
        new DotNet.DotNetModule(), new TypeScript.TypeScriptModule(), new Python.PythonModule(), new Go.GoModule(), new Rust.RustModule(), new Cpp.CppModule(), new Java.JavaModule(), new Kotlin.KotlinModule(),
        new Claude.ClaudeModule(),
    ];
}
