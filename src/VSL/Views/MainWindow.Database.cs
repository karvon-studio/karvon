using Avalonia.Controls;
using Avalonia.Interactivity;
using VSL.Database.Views;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>
/// The Database Explorer tool window (Bengine Data Studio's explorer). It is made the first time it shows: opening its
/// workspace may reconnect to data sources and ask for their passwords, which nobody wants from a VSL that never opened it.
/// </summary>
public partial class MainWindow
{
    DatabaseExplorerView? _databaseView;

    /// <summary>Where the explorer's tree and database tabs are kept (VSL's own; Studio keeps its own workspace).</summary>
    static string DatabaseWorkspaceFile => Path.Combine(Services.Settings.ConfigDirectory, "database-workspace.json");

    void InitializeDatabaseExplorer() =>
        DatabaseExplorerTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && DatabaseExplorerTab.IsSelected)
                EnsureDatabaseExplorer();
        };

    /// <summary>The explorer view, made (and its workspace restored) the first time it is needed.</summary>
    internal DatabaseExplorerView? EnsureDatabaseExplorer()
    {
        if (_databaseView is not null || Vm is not { } vm)
            return _databaseView;
        var view = _databaseView = new DatabaseExplorerView(vm.DatabaseServers) { DataContext = vm.Database };
        // Passwords, confirmations, files and the other questions the explorer asks come from the view.
        vm.Database.Dialogs = view;
        DatabaseExplorerHost.Content = view;
        view.RestoreWorkspace(DatabaseWorkspaceFile);
        return view;
    }

    /// <summary>View > Database Explorer (Ctrl+Alt+S, Visual Studio's Server Explorer key).</summary>
    public void ShowDatabaseExplorer()
    {
        Vm?.ShowToolWindow(ToolWindows.DatabaseExplorer);
        EnsureDatabaseExplorer();
    }

    void OnShowDatabaseExplorer(object? sender, RoutedEventArgs e) => ShowDatabaseExplorer();
}
