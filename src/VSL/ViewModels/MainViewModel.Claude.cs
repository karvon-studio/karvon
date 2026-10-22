using Avalonia.Threading;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude.Tools;
using VSL.Docker;
using VSL.ViewModels;

namespace VSL.ViewModels;

/// <summary>
/// Claude Code tool window: runs in the solution folder and sees the active editor and selection, and uses VSL's tools
/// (the Database Explorer's data sources, the Docker engine) as the user's own.
/// </summary>
public partial class MainViewModel : IClaudeToolHost
{
    static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    ClaudeViewModel? _claude;
    List<string>? _claudeFiles;

    public ClaudeViewModel Claude => _claude ??= CreateClaude();

    ClaudeViewModel CreateClaude()
    {
        var tools = Settings.ClaudeVslTools ? VslTools.Create(this, this, EnsureServers) : null;
        if (tools is not null)
            tools.Called += OnClaudeToolCalled;
        var claude = new ClaudeViewModel(Settings, ClaudeWorkingDirectory, ClaudeEditorContextNow, ClaudeFiles)
        {
            Tools = tools,
            McpServers = ClaudeMcpServers,
        };
        // Another account has other servers (its own .claude.json).
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeViewModel.SelectedAccount))
            {
                Mcp?.Refresh();
                Skills?.Refresh();
            }
        };
        claude.OpenFileRequested += (path, line) =>
        {
            if (line is { } l && l > 0)
                NavigateTo(path, new Lsp.LspRange(new Lsp.LspPosition(l - 1, 0), new Lsp.LspPosition(l - 1, 0)));
            else
                OpenDocument(path);
        };
        claude.DiffRequested += (path, before, after) => ShowDiff(new DiffViewModel(path, before, after, "Current", "Proposed by Claude", "claude"));
        claude.FilesChanged += () =>
        {
            _claudeFiles = null;
            CheckExternalChanges();
        };
        claude.AttentionRequested += () => ShowToolWindow(ToolWindows.Claude);
        return claude;
    }

    string? IClaudeToolHost.SolutionDirectory => Solution?.Directory;

    IReadOnlyList<ServerProfile> IClaudeToolHost.DataSources() => DatabaseServers.List();

    /// <summary>The explorer's passwords are the UI thread's.</summary>
    Task<string?> IClaudeToolHost.PasswordForAsync(ServerProfile server) =>
        Dispatcher.UIThread.InvokeAsync<string?>(() => _database is { } explorer ? explorer.PasswordFor(server) : server.Password).GetTask();

    DockerEngine IClaudeToolHost.Docker => Docker?.Engine ?? (_claudeDocker ??= DockerEngine.Find());

    DockerEngine? _claudeDocker;

    bool IClaudeToolHost.AllContainers => Settings.ClaudeAllContainers;

    string ClaudeWorkingDirectory() => Solution?.Directory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    ClaudeEditorContext? ClaudeEditorContextNow()
    {
        if (ActiveEditor is not { } doc)
            return null;
        var editor = doc.Host.Editor;
        if (editor.SelectionLength == 0)
            return new ClaudeEditorContext(doc.FilePath, null, null, null);
        var document = doc.Host.Document;
        var start = document.GetLineByOffset(editor.SelectionStart).LineNumber;
        var end = document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;
        var text = editor.SelectedText;
        return new ClaudeEditorContext(doc.FilePath, start, end, text.Length > 20000 ? text[..20000] : text);
    }

    /// <summary>Files offered for @-mentions: the solution folder without build output and VCS folders.</summary>
    IEnumerable<string> ClaudeFiles()
    {
        if (_claudeFiles is not null || Solution is not { } solution)
            return _claudeFiles ?? [];
        var files = new List<string>();
        var pending = new Stack<string>([solution.Directory]);
        while (pending.Count > 0 && files.Count < 5000)
        {
            var dir = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir));
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                        pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return _claudeFiles = files;
    }

    /// <summary>View > Claude Code (Ctrl+Esc), and Ask Claude in the editor.</summary>
    [RelayCommand]
    public void ShowClaude()
    {
        ShowToolWindow(ToolWindows.Claude);
        Claude.RefreshContext();
        Claude.RequestFocus();
    }

    /// <summary>Solution Explorer > Ask Claude: the files, folders or projects go into the prompt as @mentions.</summary>
    public void AskClaudeAbout(IReadOnlyList<string> paths)
    {
        ShowToolWindow(ToolWindows.Claude);
        Claude.RefreshContext();
        Claude.AddFiles(paths);
    }

    async Task OnClaudeSolutionChangedAsync()
    {
        _claudeFiles = null;
        // Another folder has its own .mcp.json, its own servers in Claude Code's settings, its own skills.
        Mcp?.Refresh();
        Skills?.Refresh();
        if (_claude is { } claude)
            await claude.OnWorkingDirectoryChangedAsync();
    }

    public async Task ShutdownClaudeAsync()
    {
        if (_claude is { } claude)
            await claude.DisposeAsync();
    }
}
