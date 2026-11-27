using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Karvon.Updates;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;

namespace Karvon;

public partial class App : Application
{
    public static string[] StartupArgs { get; set; } = [];

    static Services.UiWatchdog? _watchdog;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // What no code caught does not end Karvon Studio: it is logged and shown.
        Services.ErrorReports.Install();
        // A module that keeps the window's thread waiting is written down, with where it waits.
        _watchdog = Services.UiWatchdog.Start();
        Editor.FindPanel.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Modules.ModuleCatalog.Create = BuiltInModules.Create;
            Launch(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Karvon Studio's first window: the installer, a solution given on the command line, or the start window. The IDE
    /// (its modules started) comes once the vault is open: its passphrase is asked as a solution is opened, or as the start
    /// window opens one or goes on without code, before any module reads a secret.
    /// </summary>
    static void Launch(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = Services.Settings.Load();
        // Made, not started: the start window lists their templates.
        var modules = Modules.ModuleCatalog.Create();
        MainViewModel? ide = null;
        MainViewModel Ide()
        {
            if (ide is null)
            {
                ide = new MainViewModel(settings, modules);
                Services.ErrorReports.Reported += ide.ShowError;
            }
            return ide;
        }
        // As it exits: the update installed (when it is to be) and Karvon Studio started again (when asked to).
        desktop.Exit += (_, _) => Task.Run(() => AppUpdates.ExitAsync(AppUpdates.Current, settings, AppUpdates.RestartArguments)).GetAwaiter().GetResult();
        if (StartupArgs.Contains("--installer"))
        {
            // Karvon Studio Installer on its own, as Visual Studio Installer: Launch starts Karvon Studio.
            var installer = new InstallerViewModel(Ide(), AppUpdates.Current, standalone: true);
            var window = new InstallerWindow { DataContext = installer };
            installer.RestartRequested += action =>
            {
                AppUpdates.OnExit = action;
                window.Close();
            };
            installer.LaunchRequested += () =>
            {
                ShowStartWindow(desktop, settings, modules, Ide).Show();
                window.Close();
            };
            desktop.MainWindow = window;
            _ = installer.CheckForUpdatesCommand.ExecuteAsync(null);
            return;
        }
        // What the IDE would open (a folder, or a file a module that is on opens as a workspace), as MainViewModel.CanOpen says.
        var solution = StartupArgs.FirstOrDefault(arg => Directory.Exists(arg) || modules.Any(m => !settings.DisabledModules.Contains(m.Id) && m.OpensWorkspace(arg)));
        if (solution is not null && (File.Exists(solution) || Directory.Exists(solution)))
        {
            // `karvon App.sln` (or a project) opens it directly; otherwise the start window comes first, as in VS.
            Unlocked(desktop, () =>
            {
                var vm = Ide();
                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow.Show();
                _ = vm.OpenSolutionAsync(Path.GetFullPath(solution));
            });
        }
        else
            ShowStartWindow(desktop, settings, modules, Ide);
        // Each time it starts (as the settings say) the update is checked for and downloaded; a notification says it.
        _ = AppUpdates.StartAsync(AppUpdates.Current, settings);
    }

    /// <summary>
    /// The start window, as Karvon Studio's first; the vault opened and the main window once it says what to open. Its X
    /// closes Karvon Studio.
    /// </summary>
    static StartWindow ShowStartWindow(IClassicDesktopStyleApplicationLifetime desktop, Services.Settings settings, IReadOnlyList<Modules.KarvonModule> modules, Func<MainViewModel> ide)
    {
        var start = new StartWindow { DataContext = new StartViewModel(settings, templates: MainViewModel.TemplateSources(modules.Where(m => !settings.DisabledModules.Contains(m.Id)))) };
        start.ShowUpdates(AppUpdates.Current);
        start.Finished += path => Unlocked(desktop, () =>
        {
            var vm = ide();
            var main = new MainWindow { DataContext = vm };
            desktop.MainWindow = main;
            main.Show();
            if (path is not null)
                _ = vm.OpenSolutionAsync(path);
        });
        desktop.MainWindow = start;
        return start;
    }

    /// <summary>
    /// What comes next once Karvon's vault is open: its passphrase asked first when it has one and is locked (Not now goes
    /// on with it locked).
    /// </summary>
    static void Unlocked(IClassicDesktopStyleApplicationLifetime desktop, Action next)
    {
        if (Services.Keyring.Vault is not { IsLocked: true } vault)
        {
            next();
            return;
        }
        var unlock = new VaultUnlockWindow(vault);
        unlock.Finished += () =>
        {
            next();
            Avalonia.Threading.Dispatcher.UIThread.Post(unlock.Close);
        };
        // Before the start window closes: Karvon Studio does not shut down for lack of windows.
        desktop.MainWindow = unlock;
        unlock.Show();
    }
}
