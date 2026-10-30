using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude;

/// <summary>
/// Claude Code: the CLI's conversation in a tool window (see <see cref="ClaudeViewModel"/>). It runs in the solution's
/// folder, sees the editor in front and what is selected in it, and uses Karvon's tools (the core's and the modules') as
/// the user's own; what they do shows where the user would have done it. The other modules reach it as
/// <see cref="IClaudeService"/>.
/// </summary>
public sealed class ClaudeModule : KarvonModule, IClaudeService
{
    public const string ToolWindowId = "Claude";

    /// <summary>The conversation the solution had open, kept with it.</summary>
    const string ConversationKey = "claude.conversation";

    static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    ClaudeViewModel? _claude;
    List<string>? _files;
    OutputChannel? _output;

    /// <summary>The module each of the modules' tools is of: it shows what the tool did.</summary>
    readonly Dictionary<string, KarvonModule> _toolModules = new(StringComparer.Ordinal);

    public override string Id => "claude";

    public override string Name => "Claude Code";


    public override string Icon => "claude";

    public override string Description =>
        "Claude Code's conversation in Karvon: your own CLI, account and settings; the editor's file and selection, Ask Claude, "
        + "Karvon's tools for the build, tests, debugger, databases, containers and servers.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Claude Code", "Claude Code", "Claude", DockSide.Right, () => new ClaudeView { DataContext = Claude }) { Gesture = "Ctrl+Escape", Show = Show }];

    /// <summary>The conversation, made the first time it is needed.</summary>
    public ClaudeViewModel Claude => _claude ??= Create();

    /// <summary>Output (Claude): every call of Karvon's tools, what it was about and what it gave.</summary>
    public OutputChannel Output => _output ??= Workbench.Output("Claude");

    protected override void Started()
    {
        _ = Output;
        Workbench.ActiveDocumentChanged += _ => _claude?.RefreshContext();
    }

    ClaudeViewModel Create()
    {
        var tools = Workbench.Settings.ClaudeKarvonTools ? ToolServer() : null;
        if (tools is not null)
            tools.Called += OnToolCalled;
        var claude = new ClaudeViewModel(Workbench.Settings, WorkingDirectory, EditorContext, Files)
        {
            Tools = tools,
            McpServers = cwd => ExtraMcpServers?.Invoke(cwd),
        };
        // Another account has other servers (its own .claude.json) and its own skills.
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeViewModel.SelectedAccount))
                Changed?.Invoke();
        };
        claude.OpenFileRequested += (path, line) => Workbench.OpenFile(path, line ?? 0);
        claude.DiffRequested += (path, before, after) => Workbench.ShowDiff(path, before, after, "Current", "Proposed by Claude", "claude");
        claude.FilesChanged += () =>
        {
            _files = null;
            Workbench.CheckExternalChanges();
        };
        claude.AttentionRequested += () => Workbench.ShowToolWindow(ToolWindowId);
        claude.VariablesHidden += names =>
            Output.Append($"{DateTime.Now:HH:mm:ss}  Kept from Claude Code (they hold a secret): {string.Join(", ", names)}");
        return claude;
    }

    string WorkingDirectory() => Workbench.Workspace ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    ClaudeEditorContext? EditorContext() =>
        Workbench.ActiveEditorSelection() is { } selection ? new ClaudeEditorContext(selection.FilePath, selection.StartLine, selection.EndLine, selection.Text) : null;

    /// <summary>Files offered for @-mentions: the solution folder without build output and VCS folders.</summary>
    IEnumerable<string> Files()
    {
        if (_files is not null || Workbench.Workspace is not { } workspace)
            return _files ?? [];
        var files = new List<string>();
        var pending = new Stack<string>([workspace]);
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
        return _files = files;
    }

    /// <summary>
    /// Karvon's tools for a session: the core's and those of the modules that are on (each module shows what its tools did).
    /// </summary>
    public ClaudeToolServer ToolServer()
    {
        var tools = Workbench.Modules.SelectMany(m => m.ClaudeTools().Select(t => (Module: m, Tool: t))).ToList();
        foreach (var (module, tool) in tools)
            _toolModules[tool.Name] = module;
        // The shell commands the tools do go to them (the IDE's and the modules' that are on), unless the user turned it off
        // (for the sessions started after).
        var settings = Workbench.Settings;
        var server = KarvonTools.Create([.. Workbench.ClaudeTools(), .. tools.Select(t => t.Tool)],
            [.. Karvon.Claude.Tools.IdeClaudeTools.ShellRules, .. Workbench.Modules.SelectMany(m => m.ClaudeShellRules())],
            () => settings.ClaudeKarvonToolsFirst);
        // The user's rules for them, as they are at each call.
        server.Rules = () => settings.ClaudeToolRules;
        return server;
    }

    /// <summary>A call ran (on its own thread): shown on the UI thread.</summary>
    void OnToolCalled(IClaudeTool tool, JsonElement input, ClaudeToolOutput output)
    {
        var copy = input.Clone();
        Dispatcher.UIThread.Post(() => _ = ShowToolCallAsync(tool.Name, copy, output));
    }

    /// <summary>A line in Output (Claude), then what the call did, where the user would have done it.</summary>
    internal async Task ShowToolCallAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        var first = output.Text.Split('\n', 2)[0];
        Output.Append($"{DateTime.Now:HH:mm:ss}  {tool}{SubjectOf(input)}  {(output.IsError ? "failed: " : "")}{Redaction.Text(first.Length > 300 ? first[..300] + "…" : first)}");
        if (!Workbench.Settings.ClaudeShowToolActivity || output.IsError)
            return;
        try
        {
            if (_toolModules.GetValueOrDefault(tool) is { } module)
                await module.ShowClaudeToolAsync(tool, input, output);
            else
                await Workbench.ShowClaudeToolAsync(tool, input, output);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Output.Append($"          (not shown: {ex.Message})");
        }
    }

    /// <summary>What a call was about, for its line: the data source, the container, the server, the file.</summary>
    static string SubjectOf(JsonElement input)
    {
        foreach (var name in new[] { "connection", "name", "server", "service", "path", "project", "query", "filter", "action", "expression" })
            if (Str(input, name) is { Length: > 0 } value)
                return $" ({(value.Length > 80 ? value[..80] + "…" : value)})";
        return "";
    }

    /// <summary>Another folder: the conversation runs there; a solution opened gets back the conversation it had.</summary>
    public override async Task WorkspaceChangedAsync()
    {
        _files = null;
        if (_claude is { } claude)
            await claude.OnWorkingDirectoryChangedAsync();
        if (Workbench.WorkspaceState.GetValueOrDefault(ConversationKey) is { Length: > 0 } conversation)
            await Claude.RestoreConversationAsync(conversation);
    }

    /// <summary>The conversation open now, kept with the solution (one never opened this time keeps the one it would restore).</summary>
    public override void SaveWorkspaceState(IDictionary<string, string> state)
    {
        if (_claude is not { } claude)
            return;
        if (claude.SessionId is { } session)
            state[ConversationKey] = session;
        else
            state.Remove(ConversationKey);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_claude is { } claude)
            await claude.DisposeAsync();
    }

    // ---- IClaudeService -------------------------------------------------------------------------------------------

    public event Action? Changed;

    string IClaudeService.ToolWindowId => ToolWindowId;

    /// <summary>The chosen account's folder (CLAUDE_CONFIG_DIR or ~/.claude otherwise); always the home's in a home of the tests'.</summary>
    public string ConfigDirectory => Environment.GetEnvironmentVariable("KARVON_USER_HOME") is not null ? Path.Combine(Services.UserHome.Path, ".claude")
        : _claude?.SelectedAccount?.ConfigDirectory ?? ClaudeCli.ConfigDirectory;

    /// <summary>The chosen account's settings (~/.claude.json for the default one); always the home's in a home of the tests'.</summary>
    public string? SettingsFile =>
        Environment.GetEnvironmentVariable("KARVON_USER_HOME") is null && _claude?.SelectedAccount is { IsDefault: false } account
            ? Path.Combine(account.ConfigDirectory, ".claude.json") : null;

    public IReadOnlyList<IClaudeTool> BuiltInTools => Workbench.Settings.ClaudeKarvonTools ? ToolServer().Tools : [];

    public Func<string, string?>? ExtraMcpServers { get; set; }

    /// <summary>View > Claude Code (Ctrl+Esc), and Ask Claude in the editor.</summary>
    public void Show()
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.RefreshContext();
        Claude.RequestFocus();
    }

    public void Ask(string prompt)
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.Prompt = prompt;
        Claude.RequestFocus();
    }

    /// <summary>Solution Explorer > Ask Claude: the files, folders or projects go into the prompt as @mentions.</summary>
    public void AskAbout(IReadOnlyList<string> paths)
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.RefreshContext();
        Claude.AddFiles(paths);
    }

    public Task<string> AuthenticateMcpAsync(string name) => Claude.AuthenticateMcpAsync(name);

    public async Task<string?> AddMcpServerAsync(string name, string transport, string target, string scope)
    {
        var problem = await Claude.AddMcpServerAsync(name, transport, target, scope);
        Changed?.Invoke();
        return problem;
    }

    /// <summary>claude -p with a fast model and no tools, as the chosen account; the run is not kept as a session.</summary>
    public async Task<string> PrintAsync(string directory, string prompt)
    {
        var claude = Claude;
        claude.CheckCli();
        if (claude.CliPath is not { } cli)
            throw new InvalidOperationException("Claude Code is not installed: the claude command was not found.");
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-p", "--model", "haiku", "--tools", "", "--no-session-persistence", "--output-format", "text" })
            psi.ArgumentList.Add(arg);
        // Not a nested session of the Claude Code Karvon may itself be running in.
        psi.Environment.Remove("CLAUDECODE");
        claude.SelectedAccount?.Apply(psi.Environment);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("claude could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            await stdin.WriteAsync(prompt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("claude did not answer within 2 minutes.");
        }
        var (text, problem) = (await output, (await error).Trim());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(problem.Length > 0 ? problem : $"exit code {process.ExitCode}");
        return text;
    }
}
