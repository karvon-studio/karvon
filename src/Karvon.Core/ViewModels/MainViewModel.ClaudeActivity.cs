using System.Text.Json;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Modules;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.ViewModels;

/// <summary>
/// What Claude does with the core's tools, shown where the user would have done it: the build in Output and the Error List,
/// the tests in Test Explorer, a search in Find Results, a file in the editor. The side group Claude is in is left as it
/// is, so the conversation stays in view.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Asks the window for a tool window unless it would hide <paramref name="keep"/> (Claude, in the same side group).</summary>
    public event Action<string, string>? ToolWindowBesideRequested;

    void ShowToolWindowBeside(string id) => ToolWindowBesideRequested?.Invoke(id, ClaudeService?.ToolWindowId ?? "");

    /// <summary>A call of one of the core's tools ran (on the UI thread): what it did, where the user would have done it.</summary>
    Task IWorkbench.ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        ShowCoreClaudeTool(tool, input, output);
        return Task.CompletedTask;
    }

    void ShowCoreClaudeTool(string tool, JsonElement input, ClaudeToolOutput output)
    {
        switch (tool)
        {
            case "run_build":
                SelectedOutput = BuildOutput;
                // A build that failed (or could not run) shows its errors; one that succeeded, its output.
                ShowToolWindow(output.IsError ? ToolWindows.ErrorList : ToolWindows.Output);
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

    /// <summary>A file of the solution in the editor, its lines (when given) selected.</summary>
    void OpenClaudeFile(string? path, int start, int end)
    {
        if (path is null || Solution?.Directory is not { } root)
            return;
        var file = Path.GetFullPath(path, root);
        if (!Services.FilePaths.IsUnder(file, root) || !File.Exists(file))
            return;
        if (start > 0)
            // To the start of the line after the last: the last line is selected whole.
            NavigateTo(file, new Lsp.LspRange(new Lsp.LspPosition(start - 1, 0), new Lsp.LspPosition(Math.Max(end, start), 0)));
        else
            OpenDocument(file);
    }
}
