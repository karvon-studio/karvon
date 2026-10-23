using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.DotNet.Templates;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

public class ItemTemplateTests
{
    static string NewProject(string name, string? rootNamespace = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-items-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        var property = rootNamespace is null ? "" : $"<RootNamespace>{rootNamespace}</RootNamespace>";
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{property}</PropertyGroup></Project>");
        return Path.Combine(dir, name + ".csproj");
    }

    [Fact]
    public void Names_and_namespaces_follow_the_project_and_folder()
    {
        var csproj = NewProject("Shop.Api");
        var dir = Path.GetDirectoryName(csproj)!;
        var project = ProjectInfo.Load("Shop.Api", csproj);
        Assert.Equal("Shop.Api", ItemTemplates.Namespace(project, dir));
        Assert.Equal("Shop.Api.Services.Orders", ItemTemplates.Namespace(project, Path.Combine(dir, "Services", "Orders")));
        Assert.Equal("Shop.Api._2024_reports", ItemTemplates.Namespace(project, Path.Combine(dir, "2024 reports")));

        var declared = NewProject("Web", rootNamespace: "Acme.Web");
        Assert.Equal("Acme.Web.Pages", ItemTemplates.Namespace(ProjectInfo.Load("Web", declared), Path.Combine(Path.GetDirectoryName(declared)!, "Pages")));

        Assert.Equal("namespace Shop.Api;\n\npublic interface IOrders\n{\n}\n",
            ItemTemplates.Render(CSharpItemTemplates.All.Single(t => t.Name == "Interface"), "IOrders.cs", dir, project));
        Assert.Equal("<h3>OrderList</h3>\n\n@code {\n\n}\n", ItemTemplates.Render(CSharpItemTemplates.RazorComponent, "OrderList.razor", dir, project));

        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "");
        Assert.Equal("Class2.cs", ItemTemplates.SuggestName(CSharpItemTemplates.Class, dir));
        Assert.Equal("Widget.razor", ItemTemplates.FileName(CSharpItemTemplates.RazorComponent, "Widget"));
        Assert.Equal("notes.txt", ItemTemplates.FileName(CSharpItemTemplates.Class, " notes.txt "));
        Assert.Contains("already exists", ItemTemplates.Validate(dir, "Class1.cs"));
        Assert.NotNull(ItemTemplates.Validate(dir, "a/b.cs"));
        Assert.Null(ItemTemplates.Validate(dir, "Order.cs"));
    }

    [Fact]
    public void Add_new_item_suggests_a_name_until_one_is_typed()
    {
        var dir = Path.GetDirectoryName(NewProject("App"))!;
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "");
        var vm = new AddItemViewModel(dir, [.. CSharpItemTemplates.All, .. ItemTemplates.General], CSharpItemTemplates.Class);
        Assert.Equal("Class", vm.SelectedTemplate?.Name);
        Assert.Equal("Class2.cs", vm.Name);

        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Interface");
        Assert.Equal("Interface1.cs", vm.Name);
        vm.Name = "IOrders";
        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Record");
        Assert.Equal("IOrders", vm.Name);
        Assert.Equal(new NewItemChoice(CSharpItemTemplates.All.Single(t => t.Name == "Record"), "IOrders"), vm.Result);

        vm.Search = "razor";
        Assert.Equal("Razor Component", Assert.Single(vm.Templates).Name);
        Assert.Equal("Razor Component", vm.SelectedTemplate?.Name);
        vm.Search = "";
        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Class");
        vm.Name = "Class1";
        Assert.False(vm.CanAdd);
        Assert.Null(vm.Result);
    }
}

[Collection(SampleSolutionCollection.Name)]
public class SolutionExplorerTests
{
    /// <summary>Dialog answers scripted by the test (the real ones are modal windows).</summary>
    sealed class ScriptedDialogs : IDialogService
    {
        public NewItemChoice? NextItem { get; set; }

        public IReadOnlyList<string> NextFiles { get; set; } = [];

        public List<string> Confirmations { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult<bool?>(true);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult(NextItem);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(NextFiles);

        /// <summary>What the "Add a new project" wizard makes.</summary>
        public IReadOnlyList<string> NextProjects { get; set; } = [];

        /// <summary>The Reference Manager's answer, from what it offers.</summary>
        public Func<IReadOnlyList<ProjectReferenceChoice>, IReadOnlyList<string>?>? References { get; set; }

        public Task<IReadOnlyList<string>> CreateProjectAsync(string solution) => Task.FromResult(NextProjects);

        public Task<IReadOnlyList<string>?> ChooseProjectReferencesAsync(string project, IReadOnlyList<ProjectReferenceChoice> projects) =>
            Task.FromResult(References?.Invoke(projects));
    }

    /// <summary>A copy of the sample sources (no restore needed: these tests only touch files).</summary>
    internal static string CopySample()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-explorer-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return Path.Combine(dir, "BlazorSample.sln");
    }

    static IEnumerable<string> Headers(IEnumerable<object> items) => items.OfType<MenuItem>().Select(m => (string)m.Header!);

    static IEnumerable<object> Submenu(IEnumerable<object> items, string header) =>
        items.OfType<MenuItem>().Single(m => (string?)m.Header == header).Items.OfType<object>();

    [AvaloniaFact]
    public async Task Restores_the_whole_solution_state_from_the_dot_vs_folder()
    {
        var sln = CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        var longFile = Path.Combine(dir, "Web", "Long.cs");
        File.WriteAllText(longFile, string.Concat(Enumerable.Range(1, 300).Select(i => $"// line {i}\n")));
        var program = Path.Combine(dir, "Web", "Program.cs");
        var greeting = Path.Combine(dir, "Components", "Greeting.razor.cs");

        var vm = NewViewModel();
        vm.RestoresSolutionState = true;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var root = vm.ExplorerRoots[0];
        root.Children.First(n => n.Name == "Components").IsExpanded = true;
        var web = root.Children.First(n => n.Name == "Web");
        web.IsExpanded = true;
        vm.SelectedExplorerNode = web.Children.First(n => n.Name == "Program.cs");

        vm.OpenDocument(program)!.IsPinned = true;
        vm.Breakpoints.Toggle(program, 2);
        await vm.AddWatchCommand.ExecuteAsync("args");
        vm.Configuration = "Release";
        var longDoc = vm.OpenDocument(longFile)!;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        longDoc.Host.Editor.ScrollToLine(200);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        var scrolledTo = longDoc.Host.CaptureViewState().FirstVisibleLine;
        Assert.True(scrolledTo > 100, $"first visible line {scrolledTo}");
        var code = vm.OpenDocument(greeting)!.Host;
        code.Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(8, 5);
        var classBlock = code.Outlines.First(f => code.Document.GetLineByOffset(f.StartOffset).LineNumber == 5);
        classBlock.IsFolded = true;
        // Long.cs in a tab group of its own, to the right; Greeting stays the active document.
        vm.NewTabGroup(longDoc, vertical: true);
        vm.ActiveDocument = vm.Editors.Single(d => d.FileName == "Greeting.razor.cs");
        var sessionId = Guid.NewGuid().ToString();
        var transcript = FakeClaude.WriteTranscript(dir, sessionId);
        await ClaudeOf(vm).Claude.ResumeConversationCommand.ExecuteAsync(new Karvon.Claude.ClaudeConversation(sessionId, "", DateTime.Now, transcript));
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();

        // Kept next to the solution, like Visual Studio's .vs/<Solution>/v17/.suo, with paths relative to its folder.
        var file = Path.Combine(dir, ".vs", "BlazorSample", "Karvon", "state.json");
        Assert.True(File.Exists(file));
        var json = File.ReadAllText(file);
        Assert.Contains("\"Web/Program.cs\"", json);
        Assert.DoesNotContain(dir, json);
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(dir, ".vs", ".gitignore")));

        var reopened = NewViewModel();
        reopened.RestoresSolutionState = true;
        var second = new MainWindow { DataContext = reopened, Width = 1280, Height = 800 };
        second.Show();
        await reopened.OpenSolutionAsync(sln);
        var editors = reopened.Editors.ToList();
        Assert.Equal(["Program.cs", "Greeting.razor.cs", "Long.cs"], editors.Select(d => d.FileName));
        Assert.Equal(2, reopened.DocumentGroups.Count);
        Assert.True(reopened.DocumentGroupsVertical);
        Assert.Equal(["Program.cs", "Greeting.razor.cs"], reopened.DocumentGroups[0].Tabs.Select(t => t.Title));
        Assert.Equal(["Long.cs"], reopened.DocumentGroups[1].Tabs.Select(t => t.Title));
        Assert.True(editors[0].IsPinned);
        Assert.False(editors[2].IsPinned);
        Assert.Equal("Greeting.razor.cs", reopened.ActiveEditor?.FileName);
        var restored = reopened.ActiveEditor!.Host;
        Assert.Equal((8, 5), restored.CaretPosition);
        Assert.Contains(restored.Outlines, f => f.IsFolded && restored.Document.GetLineByOffset(f.StartOffset).LineNumber == 5);
        Assert.Equal([2], reopened.Breakpoints.For(program));
        Assert.Equal(["args"], reopened.Watches.Select(w => w.Expression));
        Assert.Equal("Release", reopened.Configuration);
        var reopenedRoot = reopened.ExplorerRoots[0];
        Assert.True(reopenedRoot.Children.First(n => n.Name == "Components").IsExpanded);
        Assert.True(reopenedRoot.Children.First(n => n.Name == "Web").IsExpanded);
        Assert.Equal(program, reopened.SelectedExplorerNode?.Path);
        // The Claude Code conversation is back as it was, and continues it when the user sends.
        Assert.Equal(sessionId, ClaudeOf(reopened).Claude.SessionId);
        Assert.Equal("Fix the build", Assert.IsType<UserChatItem>(ClaudeOf(reopened).Claude.Items[0]).Text);
        Assert.Equal("Fixed.", ClaudeOf(reopened).Claude.Items.OfType<TextChatItem>().Last().Text);
        // A document opens where it was scrolled to once it is shown.
        reopened.ActiveDocument = editors[2];
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
        Assert.Equal(scrolledTo, editors[2].Host.CaptureViewState().FirstVisibleLine);
        Assert.True(await reopened.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Adds_renames_and_deletes_files_like_visual_studio()
    {
        var sln = CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new ScriptedDialogs();
        vm.Dialogs = dialogs;
        vm.MoveToTrash = path => Task.FromResult(false);
        var root = vm.ExplorerRoots[0];
        var web = root.Children.First(n => n.Name == "Web");
        web.IsExpanded = true;

        // VS's context menus: Add on projects and folders, Rename and Delete on folders and files.
        var projectMenu = window.ExplorerMenuItems(web);
        Assert.Contains("Edit Project File", Headers(projectMenu));
        var add = projectMenu.OfType<MenuItem>().Single(m => (string?)m.Header == "Add");
        Assert.Equal(["New Item...", "Existing Item...", "New Folder", "New EditorConfig", "Project Reference...", "Class...", "Razor Component..."], Headers(add.Items.OfType<object>()));
        Assert.Equal(["New Item...", "Existing Item...", "New Folder", "New EditorConfig", "Class...", "Razor Component..."],
            Headers(Submenu(window.ExplorerMenuItems(web.Children.First(n => n.Kind == NodeKind.Folder)), "Add")));
        var program = web.Children.First(n => n.Name == "Program.cs");
        Assert.Contains("Rename", Headers(window.ExplorerMenuItems(program)));
        Assert.Contains("Delete", Headers(window.ExplorerMenuItems(program)));

        // Add > New Item: the chosen template becomes a file in the folder's namespace, shown, selected and opened.
        dialogs.NextItem = new NewItemChoice(CSharpItemTemplates.Class, "OrderService");
        var created = await vm.AddNewItemAsync(web);
        Assert.Equal(Path.Combine(dir, "Web", "OrderService.cs"), created);
        Assert.Equal("namespace Web;\n\npublic class OrderService\n{\n}\n", File.ReadAllText(created!));
        Assert.Equal("OrderService.cs", vm.ActiveEditor?.FileName);
        Assert.Contains(web.Children, n => n.Name == "OrderService.cs");
        Assert.Equal("OrderService.cs", (window.ExplorerTree.SelectedItem as ExplorerNode)?.Name);

        // Add > New Folder makes NewFolder1 and renames it in place.
        var folder = vm.AddNewFolder(web)!;
        Assert.Equal("NewFolder1", folder.Name);
        TextBox? RenameBox(ExplorerNode node) => (window.ExplorerTree.TreeContainerFromItem(node) as Control)?
            .GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("rename") && b.IsVisible);
        await WaitUntil(() => RenameBox(folder) is { IsFocused: true }, TimeSpan.FromSeconds(5));
        var box = RenameBox(folder)!;
        Assert.Equal("NewFolder1", box.SelectedText);
        box.Text = "Models";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Assert.True(Directory.Exists(Path.Combine(dir, "Web", "Models")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Web", "NewFolder1")));
        var models = web.Children.Single(n => n.Name == "Models");
        Assert.False(models.IsRenaming);

        // A class in a folder gets the folder in its namespace; Add Existing Item copies files in.
        dialogs.NextItem = new NewItemChoice(CSharpItemTemplates.Class, "Order");
        Assert.StartsWith("namespace Web.Models;", File.ReadAllText((await vm.AddNewItemAsync(models))!));
        var outside = Path.Combine(Path.GetTempPath(), "karvon-existing-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outside, "{}");
        dialogs.NextFiles = [outside];
        var copied = Assert.Single(await vm.AddExistingItemsAsync(models));
        Assert.Equal(Path.Combine(dir, "Web", "Models", Path.GetFileName(outside)), copied);
        Assert.Contains(models.Children, n => n.Path == copied);

        // Renaming a component takes its nested files along, and an open nested file keeps its unsaved edits.
        var components = root.Children.First(n => n.Name == "Components");
        components.IsExpanded = true;
        var greeting = components.Children.First(n => n.Name == "Greeting.razor");
        var codeBehind = vm.OpenDocument(Path.Combine(dir, "Components", "Greeting.razor.cs"))!;
        codeBehind.Host.Document.Insert(0, "// edited\n");
        Assert.True(vm.Rename(greeting, "Welcome.razor"));
        Assert.Equal(["Welcome.razor", "Welcome.razor.cs", "Welcome.razor.css"],
            Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Welcome*").Select(Path.GetFileName).Order());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Greeting*"));
        var moved = Assert.Single(vm.Editors, d => d.FileName == "Welcome.razor.cs");
        Assert.True(moved.IsDirty);
        Assert.StartsWith("// edited", moved.Host.Document.Text);
        Assert.DoesNotContain(vm.Editors, d => d.FileName == "Greeting.razor.cs");
        var welcome = components.Children.Single(n => n.Name == "Welcome.razor");
        Assert.Equal(["Welcome.razor.cs", "Welcome.razor.css"], welcome.Children.Select(c => c.Name).Order());

        // Delete asks first, then removes the file with its nested files and closes them.
        Assert.True(await vm.DeleteAsync(welcome));
        Assert.Equal(["'Welcome.razor' will be deleted."], dialogs.Confirmations);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Welcome*"));
        Assert.DoesNotContain(vm.Editors, d => d.FileName == "Welcome.razor.cs");
        Assert.DoesNotContain(components.Children, n => n.Name.StartsWith("Welcome", StringComparison.Ordinal));

        // Files created outside Karvon appear by themselves; loaded folders keep their state.
        models.IsExpanded = true;
        File.WriteAllText(Path.Combine(dir, "Web", "Notes.md"), "# Notes\n");
        await WaitUntil(() => web.Children.Any(n => n.Name == "Notes.md"), TimeSpan.FromSeconds(10));
        Assert.Contains(web.Children, n => n.Name == "Notes.md");
        Assert.Same(models, web.Children.Single(n => n.Name == "Models"));
        Assert.True(models.IsExpanded);

        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task Changes_the_solution_like_visual_studio()
    {
        var sln = CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new ScriptedDialogs();
        vm.Dialogs = dialogs;
        vm.MoveToTrash = _ => Task.FromResult(false);
        ExplorerNode Root() => vm.ExplorerRoots[0];
        ExplorerNode Node(params string[] names) => names.Aggregate(Root(), (node, name) => node.Children.Single(c => c.Name == name));
        Node("Web").IsExpanded = true;

        // The solution's context menu, as VS has it.
        var menu = window.ExplorerMenuItems(Root());
        foreach (var header in new[] { "Ask Claude", "Build Solution", "Rebuild Solution", "Clean Solution", "Run Tests", "Manage NuGet Packages for Solution...",
                     "Restore NuGet Packages", "Add", "Set Startup Project", "Create Git Repository", "Edit Solution File", "Rename", "Open in Terminal", "Copy Full Path" })
            Assert.Contains(header, Headers(menu));
        Assert.Equal(["New Project...", "Existing Project...", "New Item...", "Existing Item...", "New Solution Folder"], Headers(Submenu(menu, "Add")));
        Assert.Equal(["✓ Web"], Headers(Submenu(menu, "Set Startup Project")));
        foreach (var header in new[] { "Rename", "Remove", "Manage User Secrets", "Publish to Folder", "Pack" })
            Assert.Contains(header, Headers(window.ExplorerMenuItems(Node("Web"))));
        Assert.Contains("Run Tests", Headers(window.ExplorerMenuItems(Node("Tests"))));

        // New Solution Folder: NewFolder1, renamed in place.
        var folder = (await vm.AddSolutionFolderAsync(Root()))!;
        Assert.Equal("NewFolder1", folder.Name);
        TextBox? RenameBox(ExplorerNode node) => (window.ExplorerTree.TreeContainerFromItem(node) as Control)?
            .GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("rename") && b.IsVisible);
        await WaitUntil(() => RenameBox(folder) is { IsFocused: true }, TimeSpan.FromSeconds(5));
        var box = RenameBox(folder)!;
        box.Text = "libs";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        await WaitUntil(() => Root().Children.Any(c => c.Name == "libs"), TimeSpan.FromSeconds(5));
        Assert.Equal(NodeKind.SolutionFolder, Node("libs").Kind);
        Assert.DoesNotContain(Root().Children, c => c.Name == "NewFolder1");
        // What was expanded stays expanded when the solution is read again.
        Assert.True(Node("Web").IsExpanded);
        Assert.Contains("\"libs\", \"libs\"", File.ReadAllText(sln));

        // Add > New Project in it: the wizard's project, in the solution and selected; an existing one already there is not added twice.
        var lib = Path.Combine(dir, "Lib", "Lib.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
        File.WriteAllText(lib, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        dialogs.NextProjects = [lib];
        Assert.Equal([lib], await vm.AddNewProjectAsync(Node("libs")));
        Assert.Equal(lib, Node("libs", "Lib").Path);
        Assert.Equal(lib, (window.ExplorerTree.SelectedItem as ExplorerNode)?.Path);
        Assert.Contains(vm.Solution!.Projects, p => p.FilePath == lib);
        dialogs.NextFiles = [lib];
        Assert.Empty(await vm.AddExistingProjectsAsync(Root()));

        // Solution items: a new item is a file of the solution's folder in Solution Items, open; renamed and removed there.
        dialogs.NextItem = new NewItemChoice(ItemTemplates.TextFile, "notes");
        Assert.Equal(Path.Combine(dir, "notes.txt"), await vm.AddNewItemAsync(Root()));
        Assert.Equal("notes.txt", vm.ActiveEditor?.FileName);
        Assert.Equal(["notes.txt", "README.md"], Node("Solution Items").Children.Select(c => c.Name).Order());
        Assert.All(Node("Solution Items").Children, c => Assert.True(c.IsSolutionItem));
        Assert.Equal(["Open", "Compare with Unmodified", "Rename", "Remove", "Delete"], Headers(window.ExplorerMenuItems(Node("Solution Items", "README.md"))).Skip(1).Take(5));
        Assert.True(await vm.RenameInSolutionAsync(Node("Solution Items", "notes.txt"), "todo.txt"), vm.StatusText);
        Assert.True(File.Exists(Path.Combine(dir, "todo.txt")));
        Assert.Equal("todo.txt", vm.ActiveEditor?.FileName);
        Assert.Contains("todo.txt = todo.txt", File.ReadAllText(sln));
        Assert.True(MainViewModel.IsInSolution(Node("Solution Items", "todo.txt")));
        Assert.True(await vm.RemoveFromSolutionAsync(Node("Solution Items", "todo.txt")));
        Assert.Equal(["README.md"], Node("Solution Items").Children.Select(c => c.Name));
        Assert.True(File.Exists(Path.Combine(dir, "todo.txt")));

        // Add > Project Reference: the Reference Manager offers the other projects; dotnet add reference makes it.
        IReadOnlyList<ProjectReferenceChoice> offered = [];
        dialogs.References = choices =>
        {
            offered = choices;
            return [.. choices.Where(c => c.IsReferenced).Select(c => c.Path), lib];
        };
        Assert.True(await vm.EditProjectReferencesAsync(Node("Web").Project));
        Assert.Equal(["Components", "Lib", "Tests"], offered.Select(c => c.Name).Order());
        Assert.Contains(lib, vm.Solution!.Projects.Single(p => p.Name == "Web").ProjectReferences);
        Assert.True(Node("Web").IsExpanded);

        // Rename on a project: its project file, the solution's entry and the references to it.
        Assert.True(await vm.RenameInSolutionAsync(Node("libs", "Lib"), "Library"));
        var library = Path.Combine(dir, "Lib", "Library.csproj");
        Assert.True(File.Exists(library));
        Assert.False(File.Exists(lib));
        Assert.Equal(library, Node("libs", "Library").Path);
        Assert.Contains(library, vm.Solution!.Projects.Single(p => p.Name == "Web").ProjectReferences);
        Assert.Contains(@"..\Lib\Library.csproj", File.ReadAllText(Path.Combine(dir, "Web", "Web.csproj")));
        Assert.True(await vm.RemoveProjectReferenceAsync(vm.Solution!.Projects.Single(p => p.Name == "Web"), library));
        Assert.DoesNotContain(library, vm.Solution!.Projects.Single(p => p.Name == "Web").ProjectReferences);

        // Remove: out of the solution after asking; the files stay.
        dialogs.Confirmations.Clear();
        Assert.True(await vm.RemoveFromSolutionAsync(Node("libs", "Library")));
        Assert.True(await vm.RemoveFromSolutionAsync(Node("libs")));
        Assert.Equal(["'Library' will be removed.", "'libs' and all its contents will be removed."], dialogs.Confirmations);
        Assert.DoesNotContain(Root().Children, c => c.Name == "libs");
        Assert.True(File.Exists(library));

        // A change made outside Solution Explorer (dotnet sln, git, an editor) shows by itself.
        await SolutionEditor.AddFolderAsync(sln, null, "docs");
        await WaitUntil(() => Root().Children.Any(c => c.Name == "docs"), TimeSpan.FromSeconds(10));
        Assert.Contains(Root().Children, c => c.Name == "docs");

        // Manage User Secrets: an id for the project, its secrets.json open (in the tests' home, not the user's).
        var secrets = await vm.ManageUserSecretsAsync(Node("Web").Project);
        Assert.NotNull(secrets);
        Assert.StartsWith(Karvon.Mcp.McpConfigs.Home, secrets);
        Assert.Equal(secrets, vm.ActiveEditor?.FilePath);
        Assert.Contains("<UserSecretsId>", File.ReadAllText(Path.Combine(dir, "Web", "Web.csproj")));

        // Create Git Repository, where there is none: git init, a .gitignore for .NET, Git Changes.
        Assert.True(await vm.CreateGitRepositoryAsync());
        Assert.True(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Contains("[Bb]in/", File.ReadAllText(Path.Combine(dir, ".gitignore")));
        Assert.DoesNotContain("Create Git Repository", Headers(window.ExplorerMenuItems(Root())));

        // Rename on the solution: its file and its .vs state, opened again under the new name.
        Assert.True(await vm.RenameInSolutionAsync(Root(), "Shop"));
        Assert.Equal(Path.Combine(dir, "Shop.sln"), vm.Solution!.FilePath);
        Assert.False(File.Exists(sln));
        Assert.True(Directory.Exists(Path.Combine(dir, ".vs", "Shop")));
        Assert.Equal("Solution 'Shop'", Root().Name);

        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_folder_opens_as_it_is()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-folder-" + Guid.NewGuid().ToString("N"), "shop");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "app.ts"), "export const answer = 42;\n");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# Shop\n");
        File.WriteAllText(Path.Combine(dir, ".env"), "SECRET=1\n");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        var dialogs = new ScriptedDialogs();
        vm.Dialogs = dialogs;
        await vm.OpenSolutionAsync(dir);

        // Its files and folders, as VS's Folder View shows them; no solution, no project.
        Assert.True(vm.Solution!.IsFolder);
        Assert.Equal("shop", vm.Solution.Name);
        Assert.Equal(dir, vm.Solution.Directory);
        Assert.Equal("shop - Karvon", vm.WindowTitle);
        var root = vm.ExplorerRoots[0];
        Assert.True(root.IsFolderRoot);
        Assert.Equal(["src", "README.md"], root.Children.Select(c => c.Name));
        Assert.False(vm.HasSolutionFile);
        Assert.False(vm.BuildSolutionCommand.CanExecute(null));
        var menu = Headers(window.ExplorerMenuItems(root)).ToList();
        Assert.Contains("Add", menu);
        Assert.Contains("Open in Terminal", menu);
        Assert.DoesNotContain("Build Solution", menu);
        Assert.DoesNotContain("Rename", menu);

        // Add > New Item at the top: a file of the folder, shown and open.
        dialogs.NextItem = new NewItemChoice(ItemTemplates.TextFile, "notes");
        Assert.Equal(Path.Combine(dir, "notes.txt"), await vm.AddNewItemAsync(root));
        Assert.Contains(root.Children, c => c.Name == "notes.txt");
        Assert.Equal("notes.txt", vm.ActiveEditor?.FileName);

        // Its state is kept in its own .vs, and it is a recent one to open again.
        vm.SaveSession();
        Assert.True(File.Exists(Path.Combine(dir, ".vs", "shop", "Karvon", "state.json")));
        Assert.Equal(dir, vm.Settings.RecentSolutions[0]);
        Assert.Equal("Folder", new RecentItem(dir, null, false).IconKind);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }
}

public class SolutionStateTests
{
    [Fact]
    public void Moves_older_state_out_of_settings_and_survives_a_broken_file()
    {
        SampleSolution.EnsureConfigIsolated();
        var dir = Path.Combine(Path.GetTempPath(), "karvon-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sln = Path.Combine(dir, "Shop.slnx");
        File.WriteAllText(sln, "<Solution />");
        var settings = new Karvon.Services.Settings();
        settings.Solutions[sln] = new Karvon.Services.Settings.SolutionState
        {
            OpenDocuments = [Path.Combine(dir, "A.cs")],
            ActiveDocument = Path.Combine(dir, "A.cs"),
            Configuration = "Release",
            Breakpoints = new() { [Path.Combine(dir, "A.cs")] = [3] },
        };

        var state = Karvon.Services.SolutionState.Load(sln, settings);
        Assert.Empty(settings.Solutions);
        Assert.Equal([Path.Combine(dir, "A.cs")], state.Documents.Select(d => d.Path));
        Assert.Equal("Release", state.Configuration);
        Assert.True(state.Save(sln));
        Assert.Contains("\"A.cs\"", File.ReadAllText(Karvon.Services.SolutionState.FileFor(sln)));
        Assert.Equal([3], Karvon.Services.SolutionState.Load(sln, settings).Breakpoints[Path.Combine(dir, "A.cs")]);

        File.WriteAllText(Karvon.Services.SolutionState.FileFor(sln), "{ not json");
        Assert.Empty(Karvon.Services.SolutionState.Load(sln, settings).Documents);
    }
}
