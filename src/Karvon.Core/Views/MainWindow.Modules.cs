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
/// menu item; their dialogs over the window; how their documents look. Tools > Modules turns them on and off.
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
            foreach (var submenu in module.MainMenus)
                AddModuleSubmenu(vm, submenu);
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

    /// <summary>How many of the modules' submenus each top menu has, first in it.</summary>
    readonly Dictionary<MenuItem, int> _moduleSubmenus = [];

    /// <summary>A module's submenu, after the other modules' first in its top menu (Tools), a separator after them.</summary>
    void AddModuleSubmenu(MainViewModel vm, ModuleSubmenu submenu)
    {
        if (MainMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string header && header.Replace("_", "") == submenu.Menu) is not { } top)
            return;
        var menu = new MenuItem { Header = submenu.Header };
        foreach (var item in submenu.Items)
            menu.Items.Add(ModuleMenuControl(vm, item));
        var count = _moduleSubmenus.GetValueOrDefault(top);
        if (count == 0)
            top.Items.Insert(0, new Separator());
        top.Items.Insert(count, menu);
        _moduleSubmenus[top] = count + 1;
    }

    /// <summary>A module's menu item as a menu has it: a separator, or an item running what the module gives it.</summary>
    internal static Control ModuleMenuControl(MainViewModel vm, ModuleMenuItem item)
    {
        if (item.IsSeparator)
            return new Separator();
        var control = new MenuItem { Header = item.Header, IsEnabled = item.IsEnabled };
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
        if (_moduleTools.TryGetValue(id, out var tool) && tool.Item.IsSelected && tool.Host.Content is null && !_closing)
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

    /// <summary>
    /// Tools > Modules: which modules are on (it takes effect when Karvon Studio starts again), and what each has
    /// downloaded or is to download (installed at once, or removed when Karvon Studio starts again).
    /// </summary>
    async void OnModules(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var gray = this.FindResource("VsGrayTextBrush") as IBrush;
        var boxes = vm.AllModules.Select(module => (Module: module, Box: new CheckBox
        {
            IsChecked = !vm.Settings.DisabledModules.Contains(module.Id),
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = module.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = module.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = gray },
                },
            },
        })).ToList();
        var list = new StackPanel { Spacing = 12 };
        foreach (var (module, box) in boxes)
        {
            list.Children.Add(box);
            foreach (var component in module.Components)
                list.Children.Add(ComponentRow(vm, component, gray));
        }
        var body = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new ScrollViewer { Content = list, MaxHeight = 520 },
                new TextBlock { Text = "Modules turned on or off, and components removed, change when Karvon Studio starts again.", Foreground = gray },
            },
        };
        if (await CreateDialog("Modules", body, ("OK", true), ("Cancel", false)).ShowDialog<bool?>(this) != true)
            return;
        var disabled = boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Module.Id).ToList();
        if (disabled.Order().SequenceEqual(vm.Settings.DisabledModules.Order()))
            return;
        vm.Settings.DisabledModules = disabled;
        vm.Settings.Save();
        ((IWorkbench)vm).Status("The modules change when Karvon Studio starts again.");
    }

    /// <summary>A component of a module in Tools > Modules: what it is, whether it is there, and Install, Remove or Keep.</summary>
    internal static Control ComponentRow(MainViewModel vm, ModuleComponent component, IBrush? gray)
    {
        var state = new TextBlock { Foreground = gray, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var button = new Button { Padding = new Thickness(10, 1), MinWidth = 70, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        void Show()
        {
            (state.Text, button.Content) = vm.IsRemovalPending(component) ? ("removed when Karvon Studio starts again", "Keep")
                : component.IsInstalled() ? ($"installed, {SizeText(component.Size())}", "Remove")
                : ("downloads the first time it is needed", "Install");
            button.IsEnabled = true;
        }
        button.Click += async (_, _) =>
        {
            if (vm.IsRemovalPending(component))
                vm.KeepComponent(component);
            else if (component.IsInstalled())
                vm.RemoveComponent(component);
            else
            {
                (state.Text, button.IsEnabled) = ("installing...", false);
                if (!await vm.InstallComponentAsync(component))
                {
                    state.Text = "could not be installed: see Output (Modules)";
                    button.IsEnabled = true;
                    return;
                }
            }
            Show();
        };
        Show();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(28, 0, 0, 0), [ToolTip.TipProperty] = component.Description };
        var text = new StackPanel
        {
            Spacing = 1,
            Children = { new TextBlock { Text = $"{component.Name} {component.Version}" }, state },
        };
        grid.Children.Add(text);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    static string SizeText(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : $"{bytes / 1024.0:0} KB";
}
