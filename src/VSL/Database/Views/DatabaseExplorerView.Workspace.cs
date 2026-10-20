namespace VSL.Database.Views;

using Avalonia.Controls;
using Avalonia.Threading;
using VSL.Database.ViewModels;

/// <summary>
/// Keeps the Database Explorer's workspace across restarts: the tree, the database tabs (through the explorer) and the
/// side panel. It is VSL's own file (Bengine Data Studio keeps its own), written when VSL closes and every 20 seconds.
/// </summary>
public partial class DatabaseExplorerView
{
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? _workspacePath;
    private string _savedWorkspace = string.Empty;
    private WorkspaceState? _pendingWorkspace;

    /// <summary>
    /// Restores the workspace saved in a file once the view is in a window (a password prompt needs one to belong to).
    /// From then on the workspace is saved to the same file.
    /// </summary>
    /// <param name="path">The workspace file.</param>
    public void RestoreWorkspace(string path)
    {
        _workspacePath = path;
        var state = WorkspaceState.Load(path);
        if (state.SidePanel is 0 or 1 && this.FindControl<TabControl>("SidePanels") is { } panels)
        {
            panels.SelectedIndex = state.SidePanel;
        }

        _pendingWorkspace = state;
        RestorePendingWorkspace();
        _autosave.Tick -= OnAutosave;
        _autosave.Tick += OnAutosave;
        _autosave.Start();
    }

    private void RestorePendingWorkspace()
    {
        if (_pendingWorkspace is { } state && ViewModel is { } vm && TopLevel.GetTopLevel(this) is Window)
        {
            _pendingWorkspace = null;
            vm.RestoreWorkspace(state);
        }
    }

    /// <summary>Writes the workspace to its file when it changed since the last write.</summary>
    /// <returns>The state written (or that was already on disk), or <c>null</c> when the explorer keeps no workspace.</returns>
    public WorkspaceState? SaveWorkspace()
    {
        if (_workspacePath is null || ViewModel is not { } vm)
        {
            return null;
        }

        var state = vm.CaptureWorkspace();
        state.SidePanel = this.FindControl<TabControl>("SidePanels")?.SelectedIndex ?? 0;
        var json = state.ToJson();
        if (json == _savedWorkspace)
        {
            return state;
        }

        try
        {
            // Consoles may hold sensitive SQL: the file is the owner's only, like the server list.
            Directory.CreateDirectory(Path.GetDirectoryName(_workspacePath)!);
            var temporary = _workspacePath + ".tmp";
            File.WriteAllText(temporary, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, _workspacePath, overwrite: true);
            _savedWorkspace = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The workspace is a convenience; the next save tries again.
        }

        return state;
    }

    private void OnAutosave(object? sender, EventArgs e) => SaveWorkspace();
}
