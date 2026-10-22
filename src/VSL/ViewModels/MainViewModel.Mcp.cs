using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;
using VSL.Claude.Tools;
using VSL.Mcp;
using VSL.Mcp.ViewModels;

namespace VSL.ViewModels;

/// <summary>
/// View > MCP Servers: the servers of every agent's configuration and VSL's own (see <see cref="McpExplorerViewModel"/>),
/// made the first time the tool window shows. The Claude Code sessions VSL starts get those switched on for them.
/// </summary>
public partial class MainViewModel : IMcpHost
{
    /// <summary>The explorer, once the tool window has shown (no server is started before one is opened).</summary>
    [ObservableProperty]
    public partial McpExplorerViewModel? Mcp { get; private set; }

    public McpExplorerViewModel EnsureMcp() => Mcp ??= new McpExplorerViewModel(this);

    [RelayCommand]
    void ShowMcp()
    {
        EnsureMcp();
        ShowToolWindow(ToolWindows.Mcp);
    }

    /// <summary>VSL closes: the servers it started stop.</summary>
    public void ShutdownMcp()
    {
        Mcp?.Dispose();
        Mcp = null;
        if (_claudeMcpFile is { } file && File.Exists(file))
            File.Delete(file);
    }

    string? _claudeMcpFile;

    /// <summary>
    /// The servers a Claude Code session gets beside its own (those of Codex, Gemini CLI and VSL switched on for it),
    /// in a file of the user's alone (their environment may hold tokens); null when there are none.
    /// </summary>
    string? ClaudeMcpServers(string workingDirectory)
    {
        var servers = new McpConfigs(VSL.Services.Settings.ConfigDirectory, workingDirectory, McpClaudeFile).ForClaude();
        var file = _claudeMcpFile ??= Path.Combine(VSL.Services.Settings.ConfigDirectory, "mcp", $"claude-{Environment.ProcessId}.json");
        if (servers.Count == 0)
        {
            if (File.Exists(file))
                File.Delete(file);
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(file, new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        return file;
    }

    /// <summary>The chosen account's settings (~/.claude.json for the default one); always the home's in a home of the tests'.</summary>
    string? McpClaudeFile =>
        Environment.GetEnvironmentVariable("VSL_USER_HOME") is null && _claude?.SelectedAccount is { IsDefault: false } account
            ? Path.Combine(account.ConfigDirectory, ".claude.json") : null;

    string IMcpHost.ConfigDirectory => VSL.Services.Settings.ConfigDirectory;

    string? IMcpHost.Workspace => Solution?.Directory;

    string? IMcpHost.ClaudeFile => McpClaudeFile;

    IReadOnlyList<IClaudeTool> IMcpHost.BuiltInTools => Settings.ClaudeVslTools ? VslTools.Create(this, this, EnsureServers).Tools : [];

    IEnumerable<DocumentTab> IMcpHost.Documents => Documents;

    void IMcpHost.ShowDocument(DocumentTab document)
    {
        if (!Documents.Contains(document))
            Documents.Add(document);
        ActiveDocument = document;
    }

    void IMcpHost.CloseDocument(DocumentTab document)
    {
        if (Documents.Contains(document))
            _ = CloseDocument(document);
    }

    void IMcpHost.AskClaude(string prompt) => ((Docker.ViewModels.IDockerHost)this).AskClaude(prompt);

    void IMcpHost.OpenUrl(string url) => OpenBrowser(url);

    Task<string> IMcpHost.SignInWithClaudeAsync(string name) => Claude.AuthenticateMcpAsync(name);

    Bengine.Introspection.Servers.ICredentialStore? IMcpHost.Keyring => Bengine.Introspection.Servers.CredentialStores.System;

    void IMcpHost.Status(string text) => SetStatus(text);
}
