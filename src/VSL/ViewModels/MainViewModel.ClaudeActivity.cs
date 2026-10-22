using System.Text.Json;
using Avalonia.Threading;
using VSL.Claude;
using VSL.Claude.Tools;
using VSL.Database.ViewModels;
using VSL.Docker.ViewModels;
using static VSL.Claude.Tools.ToolInput;

namespace VSL.ViewModels;

/// <summary>
/// What Claude does with VSL's tools, shown where the user would have done it: a query's results in a Query Tool of the
/// data source, a container's logs on its page, a server's logs on its page, the build in Output and the Error List, the
/// tests in Test Explorer, a search in Find Results, a file in the editor. Each call is a line in Output (Claude). The side
/// group Claude is in is left as it is, so the conversation stays in view.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Output (Claude): every call of VSL's tools, what it was about and what it gave.</summary>
    public OutputChannel ClaudeOutput { get; } = new("Claude");

    /// <summary>Claude's Query Tool of each data source, used again while it is open.</summary>
    readonly Dictionary<string, QueryDocumentViewModel> _claudeQueries = new(StringComparer.Ordinal);

    /// <summary>Asks the window for a tool window unless it would hide <paramref name="keep"/> (Claude, in the same side group).</summary>
    public event Action<string, string>? ToolWindowBesideRequested;

    void ShowToolWindowBeside(string id) => ToolWindowBesideRequested?.Invoke(id, ToolWindows.Claude);

    /// <summary>A call ran (on its own thread): shown on the UI thread.</summary>
    void OnClaudeToolCalled(IClaudeTool tool, JsonElement input, ClaudeToolOutput output)
    {
        var copy = input.Clone();
        Dispatcher.UIThread.Post(() => _ = ShowClaudeToolAsync(tool.Name, copy, output));
    }

    internal async Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        var first = output.Text.Split('\n', 2)[0];
        ClaudeOutput.Append($"{DateTime.Now:HH:mm:ss}  {tool}{SubjectOf(input)}  {(output.IsError ? "failed: " : "")}{Redaction.Text(first.Length > 300 ? first[..300] + "…" : first)}");
        if (!Settings.ClaudeShowToolActivity || output.IsError)
            return;
        try
        {
            switch (tool)
            {
                case "db_query" when output.Detail is DbQueryRun run:
                    (await ClaudeQueryAsync(run.Server, run.Database))?.ShowRun(run.Sql, run.Results, "Claude");
                    break;
                case "db_explain" when output.Detail is DbPlanRun plan:
                    (await ClaudeQueryAsync(plan.Server, plan.Database))?.ShowPlan(plan.Sql, plan.Plan, "Claude");
                    break;
                case "db_connections" or "db_schema":
                    ShowToolWindowBeside(ToolWindows.DatabaseExplorer);
                    break;
                case "container_list" or "compose_up" or "container_remove":
                    EnsureDocker().OpenSection(DockerSection.Containers);
                    break;
                case "container_logs" or "container_inspect" or "container_restart":
                    await OpenClaudeContainerAsync(Str(input, "name"), tool == "container_inspect" ? "inspect" : null);
                    break;
                case "server_list":
                    EnsureServers();
                    ShowToolWindowBeside(ToolWindows.Servers);
                    break;
                case "server_logs" or "server_exec":
                    ShowClaudeServer(Str(input, "server"), tool == "server_logs" ? input : null);
                    break;
                case "run_build":
                    SelectedOutput = BuildOutput;
                    ShowToolWindow(output.Text.StartsWith("Build succeeded", StringComparison.Ordinal) ? ToolWindows.Output : ToolWindows.ErrorList);
                    break;
                case "build_errors":
                    ShowToolWindow(ToolWindows.ErrorList);
                    break;
                case "code_diagnostics":
                    OpenClaudeFile(Str(input, "path"), 0, 0);
                    ShowToolWindow(ToolWindows.ErrorList);
                    break;
                case "run_tests" or "test_failures":
                    ShowToolWindowBeside(ToolWindows.TestExplorer);
                    break;
                case "app_control":
                    SelectedOutput = DebugOutput;
                    ShowToolWindow(ToolWindows.Output);
                    break;
                case "debug_state":
                    ShowToolWindow(ToolWindows.CallStack);
                    break;
                case "debug_locals":
                    ShowToolWindow(ToolWindows.Locals);
                    break;
                case "open_document":
                    OpenClaudeFile(Str(input, "path"), Int(input, "startLine", 0, 0, int.MaxValue), Int(input, "endLine", 0, 0, int.MaxValue));
                    break;
                case "find_in_files" when output.Detail is IReadOnlyList<FoundText> found:
                    FindResults.Clear();
                    foreach (var hit in found)
                        FindResults.Add(new FindResultItem(hit.Path, hit.Line, hit.Column, hit.Text,
                            new Lsp.LspRange(new Lsp.LspPosition(hit.Line - 1, hit.Column - 1), new Lsp.LspPosition(hit.Line - 1, hit.Column - 1))));
                    FindResultsTitle = $"Claude: '{Str(input, "query")}' ({found.Count})";
                    ShowToolWindow(ToolWindows.FindResults);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or Bengine.Introspection.IntrospectionException)
        {
            ClaudeOutput.Append($"          (not shown: {ex.Message})");
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

    /// <summary>Claude's Query Tool of a data source: the one it had while it is open, else a new one.</summary>
    async Task<QueryDocumentViewModel?> ClaudeQueryAsync(Bengine.Introspection.Servers.ServerProfile server, string? database)
    {
        var key = server.Name + "\n" + database;
        if (_claudeQueries.TryGetValue(key, out var open) && Database.Tabs.Contains(open))
        {
            Database.ActiveTab = open;
            return open;
        }
        var query = await Database.OpenQueryAsync(server, database);
        if (query is not null)
            _claudeQueries[key] = query;
        return query;
    }

    async Task OpenClaudeContainerAsync(string? name, string? tab)
    {
        if (name is null)
            return;
        var docker = EnsureDocker();
        if (!docker.Containers.AllRows.Any())
            await docker.RefreshContainersAsync();
        if (docker.Containers.AllRows.FirstOrDefault(r => r.Container.Name == name || r.Container.Id.StartsWith(name, StringComparison.Ordinal)) is { } row)
            docker.OpenContainer(row, tab);
    }

    /// <summary>A server's page; for its logs, on its Logs tab with what Claude read (a unit, a file, how many lines).</summary>
    void ShowClaudeServer(string? name, JsonElement? logs)
    {
        if (name is null || EnsureServers().Find(name) is not { } node)
            return;
        var page = Servers!.Open(node, logs is null ? null : "logs").Page;
        if (logs is { } input)
        {
            (page.LogUnit, page.LogFile, page.LogLines) = (Str(input, "unit") ?? "", Str(input, "file") ?? "", Int(input, "lines", 200, 10, 10_000));
            page.RefreshLogsCommand.Execute(null);
        }
    }

    /// <summary>A file of the solution in the editor, its lines (when given) selected.</summary>
    void OpenClaudeFile(string? path, int start, int end)
    {
        if (path is null || Solution?.Directory is not { } root)
            return;
        var file = Path.GetFullPath(path, root);
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
            return;
        if (start > 0)
            // To the start of the line after the last: the last line is selected whole.
            NavigateTo(file, new Lsp.LspRange(new Lsp.LspPosition(start - 1, 0), new Lsp.LspPosition(Math.Max(end, start), 0)));
        else
            OpenDocument(file);
    }
}
