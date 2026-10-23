using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.ViewModels;
using Karvon.Workspace;

namespace Karvon.Views;

public partial class MainWindow : Window, IDialogService
{
    bool _closingConfirmed;

    // From the close being asked until it is done or cancelled (the Database Explorer is not made again meanwhile).
    bool _closing;
    bool _outputScrollPending;
    Key? _chord;

    public MainWindow()
    {
        InitializeComponent();
        // Cmd on a Mac where Linux and Windows have Ctrl: the window's keys and its menus' gestures.
        PlatformKeys.Apply(this, OperatingSystem.IsMacOS());
        InitializeDocking();
        ExplorerTree.AddHandler(DoubleTappedEvent, OnExplorerDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        // Before the tree item, which takes Enter for expanding.
        ExplorerTree.AddHandler(KeyDownEvent, OnExplorerKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dialogs = this;
                AttachDocumentWell(vm);
                vm.ToolWindowRequested += OnToolWindowRequested;
                vm.ToolWindowBesideRequested += OnToolWindowBesideRequested;
                AttachModules(vm);
                ApplySavedDockLayout(vm);
                vm.BreakModeEntered += () =>
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    Activate();
                };
                foreach (var channel in vm.OutputChannels)
                    channel.Appended += ScrollOutputToEnd;
                vm.ExplorerRenameRequested += BeginExplorerRename;
                vm.ExplorerSelectRequested += SelectExplorerNode;
            }
        };
        Activated += (_, _) => Vm?.CheckExternalChanges();
        Opened += (_, _) => FloatPendingToolWindows();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        RecentMenu.SubmenuOpened += (_, _) => BuildRecentMenu();
        WindowMenu.SubmenuOpened += (_, _) => UpdateWindowMenu();
        // Ctrl+wheel zooms the Output window too, independently of the editors.
        OutputEditor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!PlatformKeys.HasCommand(e.KeyModifiers) || e.Delta.Y == 0)
                return;
            OutputEditor.FontSize = Math.Clamp(OutputEditor.FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 48);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Following the output keeps its last line at the bottom; scrolling below the document would push it out of view.
        OutputEditor.Options.AllowScrollBelowDocument = false;
        OutputEditor.TextArea.TextView.LineTransformers.Add(new Editor.OutputColorizer(() => Vm?.SelectedOutput));
        // URLs stay Ctrl+clickable, in Karvon's link color instead of AvaloniaEdit's default pure blue (unreadable on dark).
        OutputEditor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.Parse("#3794FF"));
        OutputEditor.AttachedToVisualTree += (_, _) =>
        {
            if (!_outputScrollPending)
                return;
            _outputScrollPending = false;
            Dispatcher.UIThread.Post(ScrollOutputToEnd, DispatcherPriority.Background);
        };
        BuildRecentMenu();
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    Editor.EditorHost? ActiveHost => Vm?.ActiveEditor?.Host;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || Vm is not { } vm)
            return;
        e.Cancel = true;
        SaveDockLayout(vm);
        foreach (var module in vm.Modules)
            module.SaveState();
        vm.SaveSession();
        _closing = true;
        if (await vm.CloseSolutionCoreAsync())
        {
            await vm.ShutdownTerminalAsync();
            await vm.ShutdownModulesAsync();
            _closingConfirmed = true;
            Close();
        }
        else
        {
            _closing = false;
            // Not closed: a Restart asked for does not happen either.
            Updates.AppUpdates.OnExit = Updates.ExitAction.None;
        }
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // The command modifier: Ctrl, Cmd on a Mac.
        var ctrl = PlatformKeys.HasCommand(e.KeyModifiers);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        // A module's tool window key (Ctrl+Alt+S: the Database Explorer, as Visual Studio's Server Explorer).
        // Ctrl+Esc: Claude Code, as in the VS Code extension.
        if (Vm is { } toolVm && _moduleGestures.FirstOrDefault(g => g.Gesture.Matches(e)) is { Tool: { } tool })
        {
            ShowModuleToolWindow(toolVm, tool);
            e.Handled = true;
            return;
        }
        // Ctrl+` opens the terminal (VS's View.Terminal), Ctrl on a Mac too.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.OemTilde && Vm is { } terminalVm)
        {
            terminalVm.ShowTerminalCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Where a module's own keymap comes first (Bengine Data Studio's in the Database Explorer and its documents:
        // Ctrl+Shift+A is its Find Action, Ctrl+N Go to Object...), Karvon's own keys work through the window's key bindings it leaves.
        if (FocusManager?.GetFocusedElement() is Visual focusedVisual && focusedVisual.GetSelfAndVisualAncestors().OfType<Modules.IKeymapOwner>().Any())
            return;
        // Project > Add New Item (Ctrl+Shift+A) and Add Existing Item (Shift+Alt+A): into Solution Explorer's
        // selection when it has focus, else next to the active document.
        if (e.Key == Key.A && Vm is { HasSolution: true } addVm
            && (e.KeyModifiers == (PlatformKeys.Command | KeyModifiers.Shift) || e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt)))
        {
            var target = ExplorerTree.IsKeyboardFocusWithin ? ExplorerTree.SelectedItem as ExplorerNode : null;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                _ = addVm.AddExistingItemsAsync(target);
            else
                _ = addVm.AddNewItemAsync(target);
            e.Handled = true;
            return;
        }
        // Inside the terminal every other key belongs to the shell (Ctrl+R is reverse search, Ctrl+E end of line...).
        if (FocusManager?.GetFocusedElement() is Visual focused
            && (focused is Exclr8.Terminal.TerminalControl || focused.FindAncestorOfType<Exclr8.Terminal.TerminalControl>() is not null))
            return;
        // Visual Studio chords: Ctrl+R, A / Ctrl+R, L / Ctrl+R, Ctrl+R / Ctrl+E, T.
        if (_chord is { } first && Vm is { } chordVm)
        {
            _chord = null;
            e.Handled = true;
            switch (first, e.Key)
            {
                case (Key.R, Key.A) when ctrl:
                    chordVm.DebugAllTestsCommand.Execute(null);
                    break;
                case (Key.R, Key.A):
                    chordVm.RunAllTestsCommand.Execute(null);
                    break;
                case (Key.R, Key.L):
                    chordVm.RepeatLastTestRunCommand.Execute(null);
                    break;
                case (Key.R, Key.R):
                    OnRename(this, new RoutedEventArgs());
                    break;
                case (Key.E, Key.T):
                    chordVm.ShowToolWindow(ToolWindows.TestExplorer);
                    break;
            }
            return;
        }
        if (ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key is Key.R or Key.E)
        {
            _chord = e.Key;
            e.Handled = true;
            return;
        }
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.N)
        {
            _ = ShowStartWindowAsync(StartPage.NewProject);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.OemSemicolon)
        {
            ExplorerSearch.Focus();
            e.Handled = true;
        }
        // Ctrl+Tab, Ctrl on a Mac too (Cmd+Tab is the system's).
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Tab && Vm is { Documents.Count: > 1 } vm)
        {
            var i = vm.ActiveDocument is null ? 0 : vm.Documents.IndexOf(vm.ActiveDocument);
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            vm.ActiveDocument = vm.Documents[(i + step + vm.Documents.Count) % vm.Documents.Count];
            e.Handled = true;
        }
    }

    void BuildRecentMenu()
    {
        if (Vm is not { } vm)
            return;
        RecentMenu.Items.Clear();
        foreach (var path in vm.RecentSolutions)
        {
            var item = new MenuItem { Header = path, Command = vm.OpenRecentCommand, CommandParameter = path };
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    // ---- Outlining (Edit > Outlining) ------------------------------------------------------------------

    void OnToggleOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ToggleOutliningExpansion();

    void OnToggleAllOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ToggleAllOutlining();

    void OnCollapseToDefinitions(object? sender, RoutedEventArgs e) => ActiveHost?.CollapseToDefinitions();

    void OnExpandAllOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ExpandAllOutlining();

    // ---- Start window --------------------------------------------------------------------------------------

    void OnStartWindow(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.Home);

    void OnNewProject(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.NewProject);

    void OnCloneRepository(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.Clone);

    /// <summary>Opens the start window over the IDE; whatever it opens, creates or clones replaces the current solution.</summary>
    public async Task ShowStartWindowAsync(StartPage page)
    {
        if (Vm is not { } vm)
            return;
        var startVm = new StartViewModel(vm.Settings, templates: vm.ProjectTemplateSources);
        var start = new StartWindow { DataContext = startVm };
        if (page == StartPage.NewProject)
            startVm.ShowNewProjectCommand.Execute(null);
        else
            startVm.Page = page;
        var chosen = await start.ShowDialog<string?>(this);
        if (chosen is not null)
            await vm.OpenSolutionAsync(chosen);
        BuildRecentMenu();
    }

    // ---- IDialogService ----------------------------------------------------------------------------------

    public async Task<string?> PickSolutionAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Project/Solution",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("All Project Files") { Patterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"] },
                new FilePickerFileType("Solution Files") { Patterns = ["*.sln", "*.slnx"] },
                new FilePickerFileType("Project Files") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public Task<string?> PromptAsync(string title, string label, string initial) => PromptAsync(title, label, initial, secret: false);

    public async Task<string?> PromptAsync(string title, string label, string initial, bool secret)
    {
        var box = new TextBox { Text = initial, Width = 340, PasswordChar = secret ? '•' : default };
        var dialog = CreateDialog(title, new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label }, box } },
            ("OK", true), ("Cancel", false));
        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                dialog.Close(true);
        };
        var ok = await dialog.ShowDialog<bool?>(this);
        return ok == true ? box.Text : null;
    }

    public async Task<string?> PickAsync(string title, string label, IReadOnlyList<string> options, string? initial)
    {
        var list = new ListBox { ItemsSource = options, SelectedItem = initial is not null && options.Contains(initial) ? initial : options.FirstOrDefault(), MaxHeight = 320, Width = 340 };
        var dialog = CreateDialog(title, new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label }, list } }, ("OK", true), ("Cancel", false));
        list.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => list.Focus());
        list.DoubleTapped += (_, _) => dialog.Close(true);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                dialog.Close(true);
        };
        return await dialog.ShowDialog<bool?>(this) == true ? list.SelectedItem as string : null;
    }

    public Task<bool?> ConfirmSaveAsync(string fileName)
    {
        var dialog = CreateDialog("Karvon", new TextBlock { Text = $"Save changes to the following item?\n\n{fileName}", TextWrapping = TextWrapping.Wrap },
            ("Save", true), ("Don't Save", false), ("Cancel", null));
        return dialog.ShowDialog<bool?>(this);
    }

    public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) =>
        new AddItemWindow { Title = title, DataContext = new AddItemViewModel(folder, Vm?.NewItemTemplates ?? ItemTemplates.General, initial) }.ShowDialog<NewItemChoice?>(this);

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<IReadOnlyList<string>> PickProjectsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Existing Project",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Project Files") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Add > New Project: the new project wizard, adding to <paramref name="solution"/> (templates, name and location, options).</summary>
    public async Task<IReadOnlyList<string>> CreateProjectAsync(string solution)
    {
        if (Vm is not { } vm)
            return [];
        var startVm = new StartViewModel(vm.Settings, templates: vm.ProjectTemplateSources, addToSolution: solution);
        var start = new StartWindow { DataContext = startVm, Title = "Add a new project" };
        startVm.ShowNewProjectCommand.Execute(null);
        return await start.ShowDialog<string?>(this) is null ? [] : startVm.CreatedProjects;
    }

    /// <summary>Add > Project Reference: VS's Reference Manager, the solution's projects with those referenced checked.</summary>
    public async Task<IReadOnlyList<string>?> ChooseProjectReferencesAsync(string project, IReadOnlyList<ProjectReferenceChoice> projects)
    {
        var boxes = projects.Select(p => new CheckBox { Content = p.Name, IsChecked = p.IsReferenced, Tag = p.Path, [ToolTip.TipProperty] = p.Path }).ToList();
        var list = new StackPanel { Spacing = 2 };
        foreach (var box in boxes)
            list.Children.Add(box);
        Control body = projects.Count == 0
            ? new TextBlock { Text = "The solution has no other project to reference.", MaxWidth = 420, TextWrapping = TextWrapping.Wrap }
            : new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Projects", FontWeight = FontWeight.SemiBold },
                    new ScrollViewer { Content = list, MaxHeight = 360, MinWidth = 380 },
                },
            };
        var dialog = CreateDialog($"Reference Manager - {project}", body, ("OK", true), ("Cancel", false));
        return await dialog.ShowDialog<bool?>(this) == true ? boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList() : null;
    }

    public Task<bool?> ConfirmAsync(string title, string message)
    {
        var dialog = CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            ("Yes", true), ("No", false));
        return dialog.ShowDialog<bool?>(this);
    }

    void OnBranchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button button)
            return;
        var menu = new MenuFlyout();
        foreach (var branch in vm.Branches)
            menu.Items.Add(new MenuItem
            {
                Header = branch == vm.GitBranch ? "✓ " + branch : "   " + branch,
                Command = vm.SwitchBranchCommand,
                CommandParameter = branch,
            });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New Branch...", Command = vm.NewBranchCommand });
        menu.Items.Add(new MenuItem { Header = "Git Changes", Command = vm.ShowGitChangesCommand });
        menu.ShowAt(button);
    }

    void OnRunTaskMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        RunTaskMenu.Items.Clear();
        foreach (var item in RunTaskMenuItems(vm))
            RunTaskMenu.Items.Add(item);
    }

    /// <summary>Build > Run Task: each task by its name and where it comes from; one running in Output (Tasks) stops.</summary>
    internal static List<Control> RunTaskMenuItems(MainViewModel vm)
    {
        var items = new List<Control>();
        foreach (var task in vm.WorkspaceTasks)
        {
            var header = vm.IsTaskRunning(task) ? $"Stop {task.Name}" : task.Name;
            items.Add(new MenuItem
            {
                Header = task.Source.Length > 0 ? $"{header}   ({task.Source})" : header,
                Command = vm.RunTaskCommand,
                CommandParameter = task,
            });
        }
        if (items.Count == 0)
            items.Add(new MenuItem { Header = "No tasks (.vscode/tasks.json, package.json scripts)", IsEnabled = false });
        return items;
    }

    void OnLaunchProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control anchor)
            return;
        var menu = new MenuFlyout();
        foreach (var profile in vm.LaunchProfiles)
        {
            var item = new MenuItem { Header = (profile == vm.LaunchProfile ? "✓ " : "   ") + profile };
            item.Click += (_, _) => vm.LaunchProfile = profile;
            menu.Items.Add(item);
        }
        if (vm.LaunchProfiles.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No launch profiles (Properties/launchSettings.json)", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Start Without Debugging", Command = vm.StartWithoutDebuggingCommand, InputGesture = PlatformKeys.Parse("Ctrl+F5") });
        menu.ShowAt(anchor);
    }

    /// <summary>Right-click on a document tab: VS's tab context menu.</summary>
    static void OpenFolder(string folder) => Services.DesktopShell.Open(folder);

    /// <summary>Double-click in Git Changes: a file opens its diff, a folder or repository opens or closes.</summary>
    void OnGitItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitTreeRow row })
            Vm?.ActivateGitRowCommand.Execute(row);
        else if (sender is ListBox { SelectedItem: GitFileItem item })
            Vm?.OpenDiffCommand.Execute(item);
    }

    void OnGitRefreshClick(object? sender, RoutedEventArgs e) => _ = Vm?.RefreshGitAsync();

    /// <summary>The repository menu: all repositories, or one of them for Git Changes to work on.</summary>
    void OnGitRepositoryButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control anchor)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem
        {
            Header = (vm.SelectedGitRepository is null ? "✓ " : "   ") + $"All Repositories ({vm.GitRepositories.Count})",
            Command = vm.SelectGitRepositoryCommand,
            CommandParameter = null,
        });
        menu.Items.Add(new Separator());
        foreach (var repository in vm.GitRepositories)
            menu.Items.Add(new MenuItem
            {
                Header = (repository.IsSelected ? "✓ " : "   ") + repository.Name,
                Command = vm.SelectGitRepositoryCommand,
                CommandParameter = repository,
            });
        menu.ShowAt(anchor);
    }

    /// <summary>The branch menu of Git Changes: the local and remote branches of the repositories in scope.</summary>
    async void OnGitChangesBranchClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control anchor)
            return;
        var (local, remote) = await vm.ScopeBranchesAsync();
        var current = vm.GitScopeBranch;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Local", IsEnabled = false });
        foreach (var branch in local)
            menu.Items.Add(new MenuItem { Header = (branch == current ? "✓ " : "   ") + branch, Command = vm.SwitchBranchCommand, CommandParameter = branch });
        if (remote.Count > 0)
        {
            var remotes = new MenuItem { Header = "Remote" };
            foreach (var branch in remote)
                remotes.Items.Add(new MenuItem { Header = branch, Command = vm.SwitchRemoteBranchCommand, CommandParameter = branch });
            menu.Items.Add(remotes);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New Branch...", Command = vm.NewBranchCommand });
        menu.ShowAt(anchor);
    }

    Window CreateDialog(string title, Control body, params (string Text, bool? Result)[] buttons)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)this.FindResource("VsEnvironmentBrush")!,
            ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, result) in buttons)
        {
            var b = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => dialog.Close(result);
            row.Children.Add(b);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 16, Children = { body, row } };
        return dialog;
    }

    // ---- Solution Explorer ---------------------------------------------------------------------------------

    /// <summary>
    /// Double-click opens a file, also one with nested files under it (X.razor with X.razor.cs), and a project's project
    /// file, as VS does with "Open project files with double-click or the Enter key". The tree item's header handles a
    /// double-click first by toggling expansion (and marks it handled), so this listens to handled double-clicks too,
    /// opens the file and undoes that toggle: expanding a file or a project is the chevron's job. Folders still expand on
    /// double-click, as in VS.
    /// </summary>
    void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        var item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not ExplorerNode { Kind: NodeKind.File or NodeKind.Project } node)
            return;
        if (e.Handled && item.ItemCount > 0)
            node.IsExpanded = !node.IsExpanded;
        vm.OpenNode(node);
        e.Handled = true;
    }

    /// <summary>Right-click in Solution Explorer: VS's context menu for solutions, projects, folders and files.</summary>
    void OnExplorerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: ExplorerNode node } anchor || Vm is null)
            return;
        e.Handled = true;
        ExplorerTree.SelectedItem = node;
        var items = ExplorerMenuItems(node);
        if (items.Count > 0)
            new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    /// <summary>The context menu for a Solution Explorer node (separators and menu items, submenus included).</summary>
    internal List<object> ExplorerMenuItems(ExplorerNode node)
    {
        if (Vm is not { } vm)
            return [];
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture is null ? null : PlatformKeys.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }
        var path = node.Path;
        var folder = node.Kind switch
        {
            NodeKind.Solution when node.IsFolderRoot => path,
            NodeKind.Solution or NodeKind.Project => path is null ? null : Path.GetDirectoryName(path),
            NodeKind.Folder => path,
            NodeKind.File => path is null ? null : Path.GetDirectoryName(path),
            _ => null,
        };
        var items = new List<object>();
        var inSolution = vm.HasSolutionFile;
        MenuItem Menu(string header, IEnumerable<object> children, bool enabled = true)
        {
            var menu = new MenuItem { Header = header, IsEnabled = enabled };
            foreach (var child in children)
                menu.Items.Add(child);
            return menu;
        }
        // Add on a project or a folder: files into its folder (and on a project, its project references).
        MenuItem AddMenu(ProjectInfo? project) => Menu("Add",
        [
            Item("New Item...", () => _ = vm.AddNewItemAsync(node), "Ctrl+Shift+A"),
            Item("Existing Item...", () => _ = vm.AddExistingItemsAsync(node), "Shift+Alt+A"),
            Item("New Folder", () => vm.AddNewFolder(node)),
        ]);
        // Add on the solution or a solution folder: projects, solution items, solution folders.
        MenuItem SolutionAddMenu() => Menu("Add",
        [
            Item("New Project...", () => _ = vm.AddNewProjectAsync(node), enabled: inSolution),
            Item("Existing Project...", () => _ = vm.AddExistingProjectsAsync(node), enabled: inSolution),
            new Separator(),
            Item("New Item...", () => _ = vm.AddNewItemAsync(node), "Ctrl+Shift+A", enabled: inSolution),
            Item("Existing Item...", () => _ = vm.AddExistingItemsAsync(node), "Shift+Alt+A", enabled: inSolution),
            Item("New Solution Folder", () => _ = vm.AddSolutionFolderAsync(node), enabled: inSolution),
        ]);
        switch (node.Kind)
        {
            // A folder opened as it is: its files, as a folder's; none of a solution's commands.
            case NodeKind.Solution when node.IsFolderRoot:
                items.Add(AddMenu(null));
                if (!vm.HasGit)
                {
                    items.Add(new Separator());
                    items.Add(Item("Create Git Repository", () => _ = vm.CreateGitRepositoryAsync()));
                }
                break;
            case NodeKind.Solution:
                items.Add(Item("Build Solution", () => vm.BuildSolutionCommand.Execute(null), "Ctrl+Shift+B"));
                items.Add(Item("Rebuild Solution", () => vm.RebuildSolutionCommand.Execute(null)));
                items.Add(Item("Clean Solution", () => vm.CleanSolutionCommand.Execute(null)));
                items.Add(Item("Run Tests", () => vm.RunAllTestsCommand.Execute(null), enabled: vm.RunAllTestsCommand.CanExecute(null)));
                items.Add(new Separator());
                items.Add(SolutionAddMenu());
                items.Add(Menu("Set Startup Project", vm.StartupProjects.Select(p =>
                    (object)Item((p == vm.StartupProject ? "✓ " : "   ") + p.Name, () => vm.SetStartupProjectCommand.Execute(p))).ToList(), vm.StartupProjects.Count > 0));
                items.Add(new Separator());
                if (!vm.HasGit)
                    items.Add(Item("Create Git Repository", () => _ = vm.CreateGitRepositoryAsync()));
                items.Add(Item("Edit Solution File", () => vm.OpenDocument(path!), enabled: inSolution));
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2", enabled: inSolution));
                break;
            case NodeKind.SolutionFolder:
                items.Add(SolutionAddMenu());
                items.Add(new Separator());
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2", enabled: inSolution));
                items.Add(Item("Remove", () => _ = vm.RemoveFromSolutionAsync(node), "Delete", enabled: inSolution));
                break;
            case NodeKind.Project when node.Project is { } project:
                items.Add(Item("Build", () => vm.BuildProjectCommand.Execute(project)));
                items.Add(Item("Rebuild", () => vm.RebuildProjectCommand.Execute(project)));
                items.Add(Item("Clean", () => vm.CleanProjectCommand.Execute(project)));
                if (project.IsTestProject)
                    items.Add(Item("Run Tests", () => vm.RunProjectTestsCommand.Execute(project), enabled: vm.RunProjectTestsCommand.CanExecute(project)));
                items.Add(new Separator());
                items.Add(AddMenu(project));
                items.Add(Item("Set as Startup Project", () => vm.SetStartupProjectCommand.Execute(project), enabled: project.IsRunnable));
                items.Add(new Separator());
                items.Add(Item("Edit Project File", () => vm.EditProjectFile(project)));
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2", enabled: inSolution));
                items.Add(Item("Remove", () => _ = vm.RemoveFromSolutionAsync(node), "Delete", enabled: inSolution));
                break;
            // A language's Dependencies (a package.json folder's): its Package Manager, and Remove on a package.
            case NodeKind.Dependencies or NodeKind.DependencyGroup when node.Packages is { } packages:
                items.Add(Item("Manage Packages...", () => _ = vm.ManagePackagesAsync(packages)));
                break;
            case NodeKind.Package when node.Packages is { } packages:
                items.Add(Item("Manage Packages...", () => _ = vm.ManagePackagesAsync(packages)));
                items.Add(new Separator());
                items.Add(Item("Remove", () => _ = vm.RemoveDependencyAsync(node), "Delete"));
                break;
            case NodeKind.Folder when path is not null:
                // A project beside the solution's (a frontend): it can start with the startup project, as VS's multiple startup projects do.
                if (node.WorkspaceProject is { } companion)
                {
                    var start = Item("Start with the Solution", () => vm.ToggleStartWithSolutionCommand.Execute(companion), enabled: vm.RunTaskOf(companion) is not null);
                    start.ToggleType = MenuItemToggleType.CheckBox;
                    start.IsChecked = vm.StartsWithSolution(companion);
                    items.Add(start);
                    items.Add(new Separator());
                }
                items.Add(AddMenu(null));
                items.Add(new Separator());
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2"));
                items.Add(Item("Delete", () => _ = vm.DeleteAsync(node), "Delete"));
                break;
            case NodeKind.File when path is not null:
                items.Add(Item("Open", () => vm.OpenNode(node)));
                items.Add(Item("Compare with Unmodified", () =>
                {
                    if (vm.OpenDocument(path) is { } doc)
                        _ = vm.CompareWithUnmodifiedAsync(doc);
                }, enabled: vm.HasGit));
                items.Add(new Separator());
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2"));
                if (node.IsSolutionItem)
                {
                    // In the solution, not in a project: Remove takes it out of the solution, Delete off the disk too.
                    items.Add(Item("Remove", () => _ = vm.RemoveFromSolutionAsync(node), "Delete", enabled: inSolution));
                    items.Add(Item("Delete", () => _ = vm.DeleteAsync(node)));
                }
                else
                    items.Add(Item("Delete", () => _ = vm.DeleteAsync(node), "Delete"));
                break;
        }
        // What the modules add (a project's Manage NuGet Packages...): each after the item of Karvon's it names, in the
        // order the module gives them, or at the end of the submenu it names (Add), the rest after Karvon's own.
        var placed = new Dictionary<string, object>();
        foreach (var module in vm.Modules)
            foreach (var extra in module.ExplorerMenu(node))
            {
                var control = ModuleMenuControl(vm, extra);
                if (extra.In is { } submenu && items.OfType<MenuItem>().FirstOrDefault(i => i.Header is string header && header == submenu) is { } parent)
                {
                    parent.Items.Add(control);
                    continue;
                }
                var anchor = extra.After is not { } after ? null
                    : placed.TryGetValue(after, out var last) ? last
                    : items.FirstOrDefault(i => i is MenuItem { Header: string header } && header == after);
                if (anchor is null)
                    items.Add(control);
                else
                {
                    items.Insert(items.IndexOf(anchor) + 1, control);
                    placed[extra.After!] = control;
                }
            }
        if (folder is not null)
        {
            if (items.Count > 0)
                items.Add(new Separator());
            if (node.Kind != NodeKind.File)
                items.Add(Item("Open in Terminal", () => _ = vm.OpenTerminalAtAsync(folder)));
            items.Add(Item(node.Kind == NodeKind.File ? "Open Containing Folder" : "Open Folder in File Explorer", () => OpenFolder(folder)));
        }
        if (path is not null)
            items.Add(Item("Copy Full Path", () => _ = Clipboard?.SetTextAsync(path)));
        // Ask Claude: the file, folder, project or solution (its folder) as an @mention in the prompt.
        var mention = node.Kind switch
        {
            NodeKind.File or NodeKind.Folder => path,
            NodeKind.Project or NodeKind.Solution => folder,
            _ => null,
        };
        if (mention is not null && vm.HasClaude)
        {
            var ask = Item("Ask Claude", () => vm.AskClaudeAbout([mention]), "Ctrl+Escape");
            ask.Icon = new VsIcon { Kind = "Claude", Width = 14, Height = 14 };
            items.Insert(0, ask);
            items.Insert(1, new Separator());
        }
        return items;
    }

    /// <summary>Renames a node in place, as VS does: a text box over its name with the base name selected.</summary>
    void BeginExplorerRename(ExplorerNode node)
    {
        // The solution's row says "Solution 'Name'"; its name is the file's.
        node.EditName = node.Kind == NodeKind.Solution && node.Path is { } solution ? Path.GetFileNameWithoutExtension(solution) : node.Name;
        node.IsRenaming = true;
        SelectExplorerNode(node);
        Dispatcher.UIThread.Post(() =>
        {
            var box = (ExplorerTree.TreeContainerFromItem(node) as Control)?.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(b => b.Classes.Contains("rename") && b.DataContext == node);
            if (box is null)
            {
                node.IsRenaming = false;
                return;
            }
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = node.Kind == NodeKind.File && Path.GetFileNameWithoutExtension(node.Name) is { Length: > 0 } stem ? stem.Length : node.Name.Length;
        }, DispatcherPriority.Loaded);
    }

    void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ExplorerNode node })
            return;
        if (e.Key == Key.Enter)
        {
            CommitRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.IsRenaming = false;
            ExplorerTree.Focus();
            e.Handled = true;
        }
    }

    void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ExplorerNode node })
            CommitRename(node);
    }

    void CommitRename(ExplorerNode node)
    {
        if (!node.IsRenaming)
            return;
        node.IsRenaming = false;
        if (Vm is { } vm && node.EditName != node.Name)
            vm.Rename(node, node.EditName);
        ExplorerTree.Focus();
    }

    void SelectExplorerNode(ExplorerNode node)
    {
        ExplorerTree.SelectedItem = node;
        Dispatcher.UIThread.Post(() => (ExplorerTree.TreeContainerFromItem(node) as Control)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        // Keys typed into the rename box are the box's (Delete removes a character, not the file).
        if (e.Source is TextBox)
            return;
        if (e.Key == Key.F2 && ExplorerTree.SelectedItem is ExplorerNode renamed && Vm is { } renameVm)
        {
            renameVm.BeginRename(renamed);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && ExplorerTree.SelectedItem is ExplorerNode deleted && Vm is { } deleteVm)
        {
            // A project, a solution folder or a solution item leaves the solution (VS's Remove); files and folders go to the trash.
            _ = MainViewModel.IsInSolution(deleted) ? deleteVm.RemoveFromSolutionAsync(deleted) : deleteVm.DeleteAsync(deleted);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && ExplorerTree.SelectedItem is ExplorerNode node && Vm is { } vm)
        {
            // A project opens its project file, as a file opens; the rest expand.
            if (node.Kind is NodeKind.File or NodeKind.Project)
                vm.OpenNode(node);
            else
                node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    CancellationTokenSource? _searchCts;

    async void OnExplorerSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var text = ExplorerSearch.Text ?? "";
        _searchCts?.Cancel();
        if (text.Length < 2 || Vm is not { } vm)
        {
            ExplorerSearchResults.IsVisible = false;
            ExplorerTree.IsVisible = true;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            var results = await Task.Run(() => vm.SearchFiles(text).Take(500).ToList(), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            ExplorerSearchResults.ItemsSource = results;
            ExplorerSearchResults.IsVisible = true;
            ExplorerTree.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ExplorerSearchResults.SelectedItem is ExplorerNode node)
            Vm?.OpenNode(node);
    }

    void OnSyncWithActiveDocument(object? sender, RoutedEventArgs e)
    {
        ExplorerSearch.Text = "";
        if (Vm?.SyncWithActiveDocument() is { } node)
            Dispatcher.UIThread.Post(() => ExplorerTree.SelectedItem = node, DispatcherPriority.Loaded);
    }

    // ---- Tool windows ---------------------------------------------------------------------------------------

    void OnErrorDoubleTapped(object? sender, TappedEventArgs e) =>
        Vm?.NavigateToErrorCommand.Execute(ErrorListBox.SelectedItem as ErrorItem);

    void OnFindResultDoubleTapped(object? sender, TappedEventArgs e) =>
        Vm?.NavigateToFindResultCommand.Execute(FindResultsBox.SelectedItem as FindResultItem);

    void ScrollOutputToEnd()
    {
        if (OutputEditor.Document is not { } doc || Vm?.SelectedOutput.Document != doc)
            return;
        // Behind another tab the Output window has no viewport: scrolling then would leave it past the last line,
        // showing nothing once the tab is opened, so it scrolls when it is shown again.
        if (TopLevel.GetTopLevel(OutputEditor) is null)
            _outputScrollPending = true;
        else
            OutputEditor.ScrollToEnd();
    }

    void OnClearOutput(object? sender, RoutedEventArgs e) => Vm?.SelectedOutput.Clear();

    void OnShowSolutionExplorer(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.SolutionExplorer);
        ExplorerTree.Focus();
    }

    void OnShowErrorList(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.ErrorList);
    }

    void OnShowOutput(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.Output);
    }

    // ---- Edit menu -> active editor ------------------------------------------------------------------------

    void OnUndo(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.Undo();

    void OnRedo(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.Redo();

    void OnFind(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.SearchPanel.Open();

    async void OnFindInFiles(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.Solution is null)
            return;
        var initial = ActiveHost?.Editor.SelectedText ?? "";
        if (await PromptAsync("Find in Files", "Find what:", initial) is { Length: > 0 } text)
            await vm.FindInFilesAsync(text);
    }

    void OnFormatDocument(object? sender, RoutedEventArgs e) => _ = ActiveHost?.FormatDocumentAsync();

    void OnGoToDefinition(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.GoToAsync(h.Editor.CaretOffset, implementation: false);
    }

    void OnGoToImplementation(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.GoToAsync(h.Editor.CaretOffset, implementation: true);
    }

    void OnFindReferences(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.FindReferencesAsync(h.Editor.CaretOffset);
    }

    void OnRename(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
        {
            h.Editor.TextArea.Focus();
            h.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F2 });
        }
    }

    void OnExit(object? sender, RoutedEventArgs e) => Close();

    void OnResetWindowLayout(object? sender, RoutedEventArgs e) => ResetWindowLayout();

    // ---- Zoom -------------------------------------------------------------------------------------------------

    void OnZoomIn(object? sender, RoutedEventArgs e) => ActiveHost?.ZoomIn();

    void OnZoomOut(object? sender, RoutedEventArgs e) => ActiveHost?.ZoomOut();

    void OnZoomReset(object? sender, RoutedEventArgs e) => ActiveHost?.SetZoom(1.0);

    // ---- Debug menu ---------------------------------------------------------------------------------------

    void OnToggleBreakpoint(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            h.ToggleBreakpoint(h.Editor.TextArea.Caret.Line);
    }

    void OnShowLocals(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.Locals);

    void OnShowTestExplorer(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.TestExplorer);
    }

    void OnTestDoubleTapped(object? sender, TappedEventArgs e) => Vm?.NavigateToTest(TestTree.SelectedItem as TestNode);

    /// <summary>Right-click in Test Explorer: Run, Debug, Go To Test.</summary>
    void OnTestContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: TestNode node } anchor || Vm is not { } vm)
            return;
        e.Handled = true;
        vm.SelectedTest = node;
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Run", () => vm.RunSelectedTestsCommand.Execute(null), vm.RunSelectedTestsCommand.CanExecute(null)),
                Item("Debug", () => vm.DebugSelectedTestsCommand.Execute(null), vm.DebugSelectedTestsCommand.CanExecute(null)),
                new Separator(),
                Item("Go To Test", () => vm.NavigateToTest(node), node.SourcePath is not null),
            },
        }.Open(anchor);
    }

    void OnShowWatch(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.Watch);

    void OnShowCallStack(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.CallStack);


    void OnWatchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm)
            return;
        vm.AddWatchCommand.Execute(WatchInput.Text);
        WatchInput.Text = "";
        e.Handled = true;
    }

    async void OnShortcuts(object? sender, RoutedEventArgs e)
    {
        const string text = """
            Ctrl+Shift+O      Open solution
            Ctrl+S / Ctrl+Shift+S   Save / Save all
            Ctrl+Shift+B      Build solution
            F5 / Ctrl+F5      Start or Continue / Start without debugging
            Shift+F5          Stop
            Ctrl+Shift+F5     Restart
            F9                Toggle breakpoint (or click the left margin)
            F10 / F11         Step over / Step into
            Shift+F11         Step out
            Ctrl+Alt+Break    Break all
            Ctrl+. / Alt+Enter Quick Actions and Refactorings
            Ctrl+R, A         Run all tests
            Ctrl+R, Ctrl+A    Debug all tests
            Ctrl+R, L         Repeat last test run
            Ctrl+R, Ctrl+R    Rename
            Ctrl+E, T         Test Explorer
            Ctrl+Wheel        Zoom the editor (also Ctrl+Shift+. / Ctrl+Shift+, ; Ctrl+0 resets)
            Ctrl+`            Terminal
            Ctrl+Space        IntelliSense completion
            Ctrl+Shift+Space  Parameter info
            F12 / Ctrl+Click  Go to definition
            Ctrl+F12          Go to implementation
            Shift+F12         Find all references
            F2                Rename
            Ctrl+K, Ctrl+D    Format document
            Ctrl+K, Ctrl+C/U  Comment / uncomment lines
            Ctrl+F            Find in file
            Ctrl+Shift+F      Find in files
            Ctrl+;            Search Solution Explorer
            Ctrl+Tab          Next document
            Ctrl+F4           Close document
            """;
        var body = new TextBlock { Text = text, FontFamily = (FontFamily)this.FindResource("VsCodeFont")! };
        await CreateDialog("Keyboard Shortcuts", body, ("OK", true)).ShowDialog<bool?>(this);
    }

    async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var body = new TextBlock
        {
            Text = "Karvon Studio\n\nNative Avalonia IDE for C#, ASP.NET Core and Blazor.\nIntelliSense: Roslyn language server (MIT) with Razor cohosting.\nBuild/Run: .NET SDK.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        await CreateDialog("About Karvon", body, ("OK", true)).ShowDialog<bool?>(this);
    }
}
