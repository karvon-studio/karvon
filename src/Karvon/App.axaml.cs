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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Modules.ModuleCatalog.Create = BuiltInModules.Create;
            var vm = new MainViewModel();
            // As it exits: the update installed (when it is to be) and Karvon Studio started again (when asked to).
            desktop.Exit += (_, _) => Task.Run(() => AppUpdates.ExitAsync(AppUpdates.Current, vm.Settings, AppUpdates.RestartArguments)).GetAwaiter().GetResult();
            var solution = StartupArgs.FirstOrDefault(vm.CanOpen);
            if (StartupArgs.Contains("--installer"))
            {
                // Karvon Studio Installer on its own, as Visual Studio Installer: Launch starts Karvon Studio.
                var installer = new InstallerViewModel(vm, AppUpdates.Current, standalone: true);
                var window = new InstallerWindow { DataContext = installer };
                installer.RestartRequested += action =>
                {
                    AppUpdates.OnExit = action;
                    window.Close();
                };
                installer.LaunchRequested += () =>
                {
                    ShowStartWindow(desktop, vm).Show();
                    window.Close();
                };
                desktop.MainWindow = window;
                _ = installer.CheckForUpdatesCommand.ExecuteAsync(null);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (solution is not null && (File.Exists(solution) || Directory.Exists(solution)))
            {
                // `karvon App.sln` (or a project) opens it directly; otherwise the start window comes first, as in VS.
                desktop.MainWindow = new MainWindow { DataContext = vm };
                _ = vm.OpenSolutionAsync(Path.GetFullPath(solution));
            }
            else
                ShowStartWindow(desktop, vm);
            // Once a day (as the settings say) the update is checked for, and downloaded.
            _ = AppUpdates.StartAsync(AppUpdates.Current, vm.Settings);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The start window, as Karvon Studio's first; the main window once it says what to open.</summary>
    static StartWindow ShowStartWindow(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm)
    {
        var start = new StartWindow { DataContext = new StartViewModel(vm.Settings, templates: vm.ProjectTemplateSources) };
        start.Finished += path =>
        {
            var main = new MainWindow { DataContext = vm };
            desktop.MainWindow = main;
            main.Show();
            if (path is not null)
                _ = vm.OpenSolutionAsync(path);
        };
        desktop.MainWindow = start;
        return start;
    }
}
