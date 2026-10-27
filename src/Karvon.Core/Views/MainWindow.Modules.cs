using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// The modules in the window: each tool window a tab in its group (its view made the first time it shows) and a View
/// menu item; their dialogs over the window; how their documents look. Tools > Get Tools and Features (Karvon Studio
/// Installer) turns them on and off.
/// </summary>
public partial class MainWindow
{
    /// <summary>The modules whose document templates each application has already (they serve every window of it).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Application, HashSet<Type>> TemplatedModules = [];

    /// <summary>The modules' tool window keys.</summary>
    readonly List<(KeyGesture Gesture, ToolWindowInfo Tool)> _moduleGestures = [];

    void AttachModules(MainViewModel vm)
    {
        foreach (var module in vm.Modules)
        {
            module.ToolWindowReset += id => ResetModuleToolWindow(id);
            if (Application.Current is { } app && TemplatedModules.GetOrCreateValue(app).Add(module.GetType()))
                foreach (var template in module.DocumentTemplates)
                    app.DataTemplates.Add(template);
            foreach (var tool in module.ToolWindows)
                AddModuleToolWindow(vm, tool);
            foreach (var item in module.MainMenus)
                AddModuleMainMenuItem(vm, item);
            if (module.OverlayTemplate is { } overlay)
            {
                var host = new ContentControl
                {
                    ContentTemplate = overlay,
                    ZIndex = 98,
                    [!ContentProperty] = new Binding(nameof(KarvonModule.Overlay)) { Source = module },
                };
                Grid.SetColumnSpan(host, 5);
                MainGrid.Children.Add(host);
            }
        }
    }

    void AddModuleToolWindow(MainViewModel vm, ToolWindowInfo tool)
    {
        ToolWindowTabs.Register(tool);
        var host = new ContentControl();
        var item = new TabItem { Header = tool.Title, Tag = tool.Id, Content = host };
        _moduleTools[tool.Id] = (item, host, tool);
        // Made the first time it shows: a module never opened reads nothing and starts nothing.
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty)
                FillModuleToolWindow(tool.Id);
        };
        GroupOf(tool.Side).Items.Add(item);
        AttachToolTab(item);
        var menu = new MenuItem { Header = tool.MenuHeader };
        if (tool.Gesture is { } gesture)
        {
            menu.InputGesture = PlatformKeys.Parse(gesture);
            _moduleGestures.Add((menu.InputGesture, tool));
        }
        menu.Click += (_, _) => ShowModuleToolWindow(vm, tool);
        ViewMenu.Items.Insert(ViewMenu.Items.IndexOf(ViewTerminalItem), menu);
    }

    /// <summary>How many of the modules' first items each top menu has.</summary>
    readonly Dictionary<MenuItem, int> _moduleFirstItems = [];

    /// <summary>The modules' items placed after one of Karvon's, by top menu and that item's header: the last placed.</summary>
    readonly Dictionary<(MenuItem Menu, string After), object> _modulePlaced = [];

    static string Plain(object? header) => header is string text ? text.Replace("_", "") : "";

    /// <summary>
    /// A module's item of the main menu: after the item of Karvon's it names (the modules' in their order), else after the
    /// other modules' first in its top menu (Tools), a separator after them.
    /// </summary>
    void AddModuleMainMenuItem(MainViewModel vm, ModuleMainMenuItem item)
    {
        if (MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => Plain(m.Header) == item.Menu) is not { } top)
            return;
        var control = ModuleMenuControl(vm, item.Item);
        if (item.Item.After is { } after)
        {
            var anchor = _modulePlaced.TryGetValue((top, after), out var last) ? last
                : top.Items.OfType<MenuItem>().FirstOrDefault(m => Plain(m.Header) == Plain(after));
            if (anchor is not null)
            {
                top.Items.Insert(top.Items.IndexOf(anchor) + 1, control);
                _modulePlaced[(top, after)] = control;
                return;
            }
        }
        var count = _moduleFirstItems.GetValueOrDefault(top);
        if (count == 0)
            top.Items.Insert(0, new Separator());
        top.Items.Insert(count, control);
        _moduleFirstItems[top] = count + 1;
    }

    /// <summary>A module's menu item as a menu has it: a separator, a submenu, or an item running what the module gives it.</summary>
    internal static Control ModuleMenuControl(MainViewModel vm, ModuleMenuItem item)
    {
        if (item.IsSeparator)
            return new Separator();
        var control = new MenuItem { Header = item.Header, IsEnabled = item.IsEnabled };
        if (item.Items is { } items)
        {
            foreach (var child in items)
                control.Items.Add(ModuleMenuControl(vm, child));
            return control;
        }
        control.Click += async (_, _) =>
        {
            try
            {
                await item.Run!();
            }
            catch (Exception ex)
            {
                ((IWorkbench)vm).Status(ex.Message);
            }
        };
        return control;
    }

    /// <summary>A module's tool window in front, as its module shows it.</summary>
    static void ShowModuleToolWindow(MainViewModel vm, ToolWindowInfo tool)
    {
        if (tool.Show is { } show)
            show();
        else
            vm.ShowToolWindow(tool.Id);
    }

    readonly Dictionary<string, (TabItem Item, ContentControl Host, ToolWindowInfo Tool)> _moduleTools = [];

    /// <summary>A module's tool window that shows gets its view, when it has none and the module has one to give.</summary>
    void FillModuleToolWindow(string id)
    {
        if (_moduleTools.TryGetValue(id, out var tool) && IsInFront(tool.Item) && tool.Host.Content is null && !_closing)
            tool.Host.Content = tool.Tool.CreateView();
    }

    /// <summary>The module made its view stale (another solution's explorer): it goes, and the next one comes when it shows.</summary>
    void ResetModuleToolWindow(string id)
    {
        if (!_moduleTools.TryGetValue(id, out var tool))
            return;
        tool.Host.Content = null;
        FillModuleToolWindow(id);
    }

    InstallerWindow? _installer;

    /// <summary>Tools > Get Tools and Features: Karvon Studio Installer's Modify, as Visual Studio's.</summary>
    void OnGetToolsAndFeatures(object? sender, RoutedEventArgs e) => ShowInstaller(InstallerPage.Modify, check: false);

    /// <summary>Help > Check for Updates: the installer, checking.</summary>
    void OnCheckForUpdates(object? sender, RoutedEventArgs e) => ShowInstaller(InstallerPage.Product, check: true);

    void OnUpdateBadgeClick(object? sender, RoutedEventArgs e) => ShowInstaller(InstallerPage.Product, check: false);

    /// <summary>Karvon Studio Installer over the window (one: shown again when it is open), on its product or on Modify.</summary>
    internal InstallerWindow? ShowInstaller(InstallerPage page, bool check)
    {
        if (Vm is not { } vm)
            return null;
        if (_installer is not { DataContext: InstallerViewModel installer } window)
        {
            installer = new InstallerViewModel(vm, vm.Updates);
            installer.RestartRequested += Restart;
            window = new InstallerWindow { DataContext = installer };
            window.Closed += (_, _) => _installer = null;
            _installer = window;
            window.Show(this);
        }
        else
            window.Activate();
        if (page == InstallerPage.Modify)
            installer.ShowModifyCommand.Execute(null);
        else
            installer.ShowProductCommand.Execute(null);
        if (check)
            _ = installer.CheckForUpdatesCommand.ExecuteAsync(null);
        return window;
    }

    /// <summary>
    /// Restart (and update): Karvon Studio closes as it always does (asking about what is not saved), then starts again
    /// with the solution that is open, updated when asked to.
    /// </summary>
    void Restart(Updates.ExitAction action)
    {
        Updates.AppUpdates.OnExit = action;
        Updates.AppUpdates.RestartArguments = Vm?.Solution?.FilePath is { } open ? [open] : [];
        _installer?.Close();
        Close();
    }
}
