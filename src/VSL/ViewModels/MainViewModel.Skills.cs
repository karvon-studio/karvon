using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;
using VSL.Mcp;
using VSL.Skills.ViewModels;

namespace VSL.ViewModels;

/// <summary>
/// View > Skills: the skills, subagents, commands and hooks Claude Code reads, and the memory files (see
/// <see cref="SkillsExplorerViewModel"/>), made the first time the tool window shows.
/// </summary>
public partial class MainViewModel : ISkillsHost
{
    /// <summary>The explorer, once the tool window has shown.</summary>
    [ObservableProperty]
    public partial SkillsExplorerViewModel? Skills { get; private set; }

    public SkillsExplorerViewModel EnsureSkills() => Skills ??= new SkillsExplorerViewModel(this);

    [RelayCommand]
    void ShowSkills()
    {
        EnsureSkills();
        ShowToolWindow(ToolWindows.Skills);
    }

    public void ShutdownSkills()
    {
        Skills?.Dispose();
        Skills = null;
    }

    string? ISkillsHost.Workspace => Solution?.Directory;

    /// <summary>The chosen account's folder (CLAUDE_CONFIG_DIR or ~/.claude otherwise); always the home's in a home of the tests'.</summary>
    string ISkillsHost.ClaudeDirectory => Environment.GetEnvironmentVariable("VSL_USER_HOME") is not null ? Path.Combine(McpConfigs.Home, ".claude")
        : _claude?.SelectedAccount?.ConfigDirectory ?? ClaudeCli.ConfigDirectory;

    VSL.Services.Settings ISkillsHost.Settings => Settings;

    void ISkillsHost.SaveSettings() => Settings.Save();

    IEnumerable<DocumentTab> ISkillsHost.Documents => Documents;

    void ISkillsHost.ShowDocument(DocumentTab document) => ((Mcp.ViewModels.IMcpHost)this).ShowDocument(document);

    void ISkillsHost.CloseDocument(DocumentTab document) => ((Mcp.ViewModels.IMcpHost)this).CloseDocument(document);

    void ISkillsHost.OpenFile(string path, int line)
    {
        if (line > 0)
            NavigateTo(path, new Lsp.LspRange(new Lsp.LspPosition(line - 1, 0), new Lsp.LspPosition(line - 1, 0)));
        else
            OpenDocument(path);
    }

    void ISkillsHost.AskClaude(string prompt) => ((Docker.ViewModels.IDockerHost)this).AskClaude(prompt);

    void ISkillsHost.OpenUrl(string url) => OpenBrowser(url);

    /// <summary>Claude Code has it already (in any of its settings, at that URL's host), or `claude mcp add --scope user` adds it.</summary>
    async Task<string?> ISkillsHost.EnsureClaudeMcpServerAsync(string name, string url)
    {
        var host = new Uri(url).Host;
        var configs = new McpConfigs(VSL.Services.Settings.ConfigDirectory, Solution?.Directory, McpClaudeFile);
        if (configs.Read().Servers.Any(s => McpConfigs.ClaudeReadsItself(s.Origin) && s.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var at) && at.Host == host))
            return null;
        var problem = await Claude.AddMcpServerAsync(name, "http", url, "user");
        Mcp?.Refresh();
        return problem;
    }

    void ISkillsHost.Status(string text) => SetStatus(text);
}
