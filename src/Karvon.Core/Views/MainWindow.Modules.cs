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

    /// <summary>Tools > Modules: which modules are on. It takes effect when Karvon Studio starts again.</summary>
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
        foreach (var (_, box) in boxes)
            list.Children.Add(box);
        var body = new StackPanel
        {
            Spacing = 16,
            Children = { list, new TextBlock { Text = "Changes take effect when Karvon Studio starts again.", Foreground = gray } },
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
}
