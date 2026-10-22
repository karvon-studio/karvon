using Avalonia.Controls;
using Avalonia.Interactivity;
using Karvon.Database.Views;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// The Database Explorer tool window (Bengine Data Studio's explorer). It is made the first time it shows: opening its
/// workspace may reconnect to data sources and ask for their passwords, which nobody wants from a Karvon that never opened it.
/// Each solution has its own (see <see cref="MainViewModel.DatabaseDirectory"/>): the view goes with its solution, and the
/// next solution's is made when it shows.
/// </summary>
public partial class MainWindow
{
    DatabaseExplorerView? _databaseView;

    void InitializeDatabaseExplorer() =>
        DatabaseExplorerTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && DatabaseExplorerTab.IsSelected)
                EnsureDatabaseExplorer();
        };

    void AttachDatabaseExplorer(MainViewModel vm)
    {
        vm.DatabaseClosing += () =>
        {
            _databaseView?.CloseWorkspace();
            DatabaseExplorerHost.Content = null;
            _databaseView = null;
        };
        vm.DatabaseChanged += () =>
        {
            if (DatabaseExplorerTab.IsSelected)
                EnsureDatabaseExplorer();
        };
    }

    /// <summary>The explorer view of the open solution, made (and its workspace restored) the first time it is needed.</summary>
    internal DatabaseExplorerView? EnsureDatabaseExplorer()
    {
        // Between two solutions the explorer is nobody's: the next one's shows once it is open.
        if (_databaseView is not null || Vm is not { IsOpeningSolution: false } vm || _closing)
            return _databaseView;
        var view = _databaseView = new DatabaseExplorerView(vm.DatabaseServers) { DataContext = vm.Database };
        // Passwords, confirmations, files and the other questions the explorer asks come from the view.
        vm.Database.Dialogs = view;
        DatabaseExplorerHost.Content = view;
        view.RestoreWorkspace(vm.DatabaseWorkspaceFile);
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
