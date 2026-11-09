using System.Text.Json;
using Avalonia.Threading;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude;

/// <summary>
/// What a module whose tool window is a conversation with an agent (Claude Code's, the other agents') has alike: the
/// solution's folder and files, the editor's selection, Karvon's tools (the core's and the modules' that are on) and an
/// Output channel where every call of them is written, then shown where the user would have done it.
/// </summary>
public abstract class AgentPanelModule : KarvonModule
{
    static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    List<string>? _files;
    OutputChannel? _output;

    /// <summary>The module each of the modules' tools is of: it shows what the tool did.</summary>
    readonly Dictionary<string, KarvonModule> _toolModules = new(StringComparer.Ordinal);

    /// <summary>Its Output channel's name.</summary>
    protected abstract string OutputName { get; }

    /// <summary>Output (its channel): every call of Karvon's tools, what it was about and what it gave.</summary>
    public OutputChannel Output => _output ??= Workbench.Output(OutputName);

    protected string WorkingDirectory() => Workbench.Workspace ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    protected ClaudeEditorContext? EditorContext() =>
        Workbench.ActiveEditorSelection() is { } selection ? new ClaudeEditorContext(selection.FilePath, selection.StartLine, selection.EndLine, selection.Text) : null;

    /// <summary>The files are listed again when next asked for (another folder, the agent changed some).</summary>
    protected void ForgetFiles() => _files = null;

    /// <summary>Files offered for @-mentions: the solution folder without build output and VCS folders.</summary>
    protected IEnumerable<string> Files()
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

    /// <summary>The panel's tool server, its calls shown as they run; null when the user keeps Karvon's tools from agents.</summary>
    protected ClaudeToolServer? NewToolServer()
    {
        if (!Workbench.Settings.ClaudeKarvonTools)
            return null;
        var server = ToolServer();
        server.Called += OnToolCalled;
        return server;
    }

    /// <summary>What the panel asks of the window: files and diffs opened, links followed, the panel brought forward.</summary>
    /// <param name="agent">Who the panel talks to, for Output's lines ("Claude Code").</param>
    /// <param name="proposer">Who proposes a change, in the diff's title ("Claude"), as the panel says it then.</param>
    /// <param name="diffSource">The diff's kind, as the window keeps it.</param>
    protected void Wire(ClaudeViewModel panel, string toolWindowId, string agent, Func<string> proposer, string diffSource)
    {
        // The window's, not the panel's: a sign-in started elsewhere opens its page with the panel closed too.
        panel.Confirm = Workbench.ConfirmAsync;
        panel.OpenUrlRequested += url =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                Workbench.OpenUrl(url);
        };
        panel.OpenFileRequested += (path, line) => Workbench.OpenFile(path, line ?? 0);
        panel.DiffRequested += (path, before, after) => Workbench.ShowDiff(path, before, after, "Current", "Proposed by " + proposer(), diffSource);
        panel.FilesChanged += () =>
        {
            _files = null;
            Workbench.CheckExternalChanges();
        };
        panel.AttentionRequested += () => Workbench.ShowToolWindow(toolWindowId);
        panel.VariablesHidden += names =>
            Output.Append($"{DateTime.Now:HH:mm:ss}  Kept from {agent} (they hold a secret): {string.Join(", ", names)}");
    }

    /// <summary>A call ran (on its own thread): shown on the UI thread.</summary>
    void OnToolCalled(IClaudeTool tool, JsonElement input, ClaudeToolOutput output)
    {
        var copy = input.Clone();
        Dispatcher.UIThread.Post(() => _ = ShowToolCallAsync(tool.Name, copy, output));
    }

    /// <summary>A line in Output, then what the call did, where the user would have done it.</summary>
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
}
