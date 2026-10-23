using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.Build;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>
/// The README screenshots (docs/screenshots), rendered headlessly from a small todo app built on the sample solution.
/// Skipped unless KARVON_SCREENSHOTS names the folder to write them to:
/// <c>KARVON_SCREENSHOTS=$PWD/docs/screenshots dotnet tests/Karvon.Tests/bin/Debug/net10.0/Karvon.Tests.dll -class Karvon.Tests.Screenshots</c>
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class Screenshots
{
    const int Width = 1280;
    const int Height = 800;

    static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("KARVON_SCREENSHOTS");
    static readonly Lazy<Task<string>> Demo = new(PrepareDemoAsync);

    const string ProgramCs = """
        using Web;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<TodoStore>();

        var app = builder.Build();
        var store = app.Services.GetRequiredService<TodoStore>();
        var open = store.All().Count(t => !t.Done);
        app.Logger.LogInformation("{Open} todos to do", open);

        app.MapGet("/", () => "Hello from the Karvon sample");
        app.MapGet("/todos", (TodoStore todos) => todos.All());
        app.MapPost("/todos", (TodoStore todos, string title) => todos.Add(title));

        app.Run();

        """;

    const string TodoStoreCs = """
        namespace Web;

        public record Todo(int Id, string Title, bool Done);

        /// <summary>The todo list, kept in memory.</summary>
        public class TodoStore
        {
            readonly List<Todo> _todos = [new(1, "Try Karvon", true), new(2, "Ship it", false)];

            public IReadOnlyList<Todo> All() => _todos;

            public Todo Add(string title)
            {
                var todo = new Todo(_todos.Count + 1, title, false);
                _todos.Add(todo);
                return todo;
            }

            public bool Remove(int id) => _todos.RemoveAll(t => t.Id == id) > 0;
        }

        """;

    /// <summary>The sample solution plus the todo app, committed to a fresh repository and restored.</summary>
    static async Task<string> PrepareDemoAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-demo", "BlazorSample");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        File.WriteAllText(Path.Combine(dir, "Web", "Program.cs"), ProgramCs);
        File.WriteAllText(Path.Combine(dir, "Web", "TodoStore.cs"), TodoStoreCs);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        TempRepo.Git(dir, "init", "-q", "-b", "main");
        // Git Changes shows who commits: the demo's own identity, never the user's.
        TempRepo.Git(dir, "config", "user.name", "Karvon Demo");
        TempRepo.Git(dir, "config", "user.email", "demo@example.invalid");
        TempRepo.Git(dir, "add", "-A");
        TempRepo.Git(dir, "-c", "user.name=Karvon", "-c", "user.email=karvon@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Todo app");

        await SampleSolution.RunDotnetAsync("Restoring the demo solution", "restore", Path.Combine(dir, "BlazorSample.sln"));
        return Path.Combine(dir, "BlazorSample.sln");
    }

    static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        UiTests.SaveFrame(window, Path.Combine(OutputDirectory!, name + ".png"));
    }

    static Task Settle() => WaitUntil(() => false, TimeSpan.FromMilliseconds(400));

    /// <summary>The main window on the demo solution, with Solution Explorer showing both app projects.</summary>
    static async Task<(MainViewModel Vm, MainWindow Window, string Dir)> OpenDemoAsync()
    {
        Assert.SkipWhen(OutputDirectory is null, "Set KARVON_SCREENSHOTS to the output folder to render the README screenshots.");
        Directory.CreateDirectory(OutputDirectory!);
        var sln = await Demo.Value;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        foreach (var project in vm.ExplorerRoots[0].Children.Where(n => n.Name is "Web" or "Components"))
            project.IsExpanded = true;
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));
        return (vm, window, Path.GetDirectoryName(sln)!);
    }

    static async Task CloseAsync(MainViewModel vm, MainWindow window)
    {
        foreach (var doc in vm.Documents.OfType<DocumentViewModel>())
            while (doc.Host.Editor.CanUndo)
                doc.Host.Editor.Undo();
        window.ResetWindowLayout();
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Editor()
    {
        var (vm, window, dir) = await OpenDemoAsync();
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        vm.SelectedOutput = vm.BuildOutput;
        vm.ShowToolWindow(ToolWindows.Output);
        var host = vm.OpenDocument(Path.Combine(dir, "Web", "TodoStore.cs"))!.Host;
        await Settle();

        host.Editor.CaretOffset = host.Document.Text.IndexOf("_todos;", StringComparison.Ordinal) + "_todos;".Length;
        host.Editor.TextArea.PerformTextInput("\n\n    public int Open => _todos.");
        // Roslyn can answer the first request after an edit with an empty list.
        for (var i = 0; i < 10 && host.CompletionItemCount == 0; i++)
        {
            await WaitUntil(() => host.CompletionItemCount > 0, TimeSpan.FromSeconds(3));
            if (host.CompletionItemCount == 0)
                host.RequestCompletion(".");
        }
        Assert.True(host.CompletionItemCount > 0);
        Save(window, "editor");
        host.CloseCompletion();
        await CloseAsync(vm, window);
    }

    [AvaloniaFact]
    public async Task Debugging()
    {
        var (vm, window, dir) = await OpenDemoAsync();
        var program = Path.Combine(dir, "Web", "Program.cs");
        var host = vm.OpenDocument(program)!.Host;
        host.ToggleBreakpoint(9);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0 && vm.Locals.Count > 0 && host.ExecutionLine == 9, TimeSpan.FromMinutes(2));
        Assert.True(host.ExecutionLine == 9, "did not break; output:\n" + vm.DebugOutput.Document.Text);
        await vm.AddWatchCommand.ExecuteAsync("store.All().Count");
        vm.ShowToolWindow(ToolWindows.Locals);
        await Settle();
        Save(window, "debugging");

        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        host.ToggleBreakpoint(9);
        vm.RemoveWatchCommand.Execute(vm.Watches.Single());
        await CloseAsync(vm, window);
    }

    [AvaloniaFact]
    public async Task Git()
    {
        var (vm, window, dir) = await OpenDemoAsync();
        var program = Path.Combine(dir, "Web", "Program.cs");
        var store = Path.Combine(dir, "Web", "TodoStore.cs");
        try
        {
            File.WriteAllText(program, ProgramCs
                .Replace("app.MapGet(\"/\", () => \"Hello from the Karvon sample\");\n", "")
                .Replace("todos.Add(title));", "todos.Add(title));\napp.MapDelete(\"/todos/{id:int}\", (TodoStore todos, int id) =>\n    todos.Remove(id) ? Results.NoContent() : Results.NotFound());"));
            File.WriteAllText(store, TodoStoreCs.Replace("_todos.Count + 1", "_todos.Max(t => t.Id) + 1"));
            await WaitUntil(() => vm.UnstagedChanges.Count == 2, TimeSpan.FromSeconds(10));
            await vm.OpenDiffCommand.ExecuteAsync(vm.UnstagedChanges.First(c => c.FileName == "Program.cs"));
            vm.CommitMessage = "Delete todos by id";
            vm.ShowToolWindow(ToolWindows.GitChanges);
            await Settle();
            Save(window, "git");
        }
        finally
        {
            TempRepo.Git(dir, "checkout", "--", ".");
        }
        await WaitUntil(() => vm.GitChangeCount == 0, TimeSpan.FromSeconds(10));
        vm.CommitMessage = "";
        await CloseAsync(vm, window);
    }

    [AvaloniaFact]
    public async Task Test_explorer()
    {
        var (vm, window, _) = await OpenDemoAsync();
        await vm.RunAllTestsCommand.ExecuteAsync(null);
        var failing = vm.TestRoots.Single().Children.Single().Children.Single().Children.Single(c => c.Name == "Deliberately_failing");
        vm.NavigateToTest(failing);
        await WaitUntil(() => vm.ActiveEditor?.FileName == "GreetingTests.cs", TimeSpan.FromSeconds(5));
        vm.SelectedTest = failing;
        vm.ShowToolWindow(ToolWindows.TestExplorer);
        await Settle();
        Save(window, "test-explorer");
        await CloseAsync(vm, window);
    }

    [AvaloniaFact]
    public async Task Claude_code()
    {
        var (vm, window, dir) = await OpenDemoAsync();
        var host = vm.OpenDocument(Path.Combine(dir, "Web", "Program.cs"))!.Host;
        host.Editor.CaretOffset = host.Document.GetLineByNumber(13).Offset;
        window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[4].Width = new GridLength(560);
        vm.ShowClaude();
        var claude = ClaudeOf(vm).Claude;
        await WaitUntil(() => claude.Commands.Count > 0, TimeSpan.FromSeconds(10));
        claude.RefreshContext();
        claude.Prompt = "Add an endpoint that deletes a todo by id";
        await claude.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => claude.Items.OfType<PermissionChatItem>().Any(), TimeSpan.FromSeconds(10));
        await Settle();
        Save(window, "claude-code");

        await claude.DenyCommand.ExecuteAsync(claude.Items.OfType<PermissionChatItem>().Single());
        await WaitUntil(() => !claude.IsRunning, TimeSpan.FromSeconds(10));
        await ClaudeOf(vm).DisposeAsync();
        await CloseAsync(vm, window);
    }

    [AvaloniaFact]
    public async Task Docker_explorer()
    {
        var (vm, window, dir) = await OpenDemoAsync();
        // The demo's own compose stack, beside a cache and a migration that failed (the fake docker command line).
        var compose = Path.Combine(dir, "compose.yaml");
        File.WriteAllText(compose, "services:\n  web:\n    image: nginx\n");
        var (docker, _, _) = FakeDocker.Create(FakeDocker.Sample(dir));
        var engine = Environment.GetEnvironmentVariable("KARVON_DOCKER_PATH");
        Environment.SetEnvironmentVariable("KARVON_DOCKER_PATH", docker);
        try
        {
            // Room for the dashboard: a smaller Error List.
            window.FindControl<Grid>("LeftGrid")!.RowDefinitions[2].Height = new GridLength(120);
            var module = vm.Module<Karvon.Docker.DockerModule>()!;
            vm.ShowToolWindow(Karvon.Docker.DockerModule.ToolWindowId);
            var explorer = module.Explorer!;
            await WaitUntil(() => explorer.IsEngineRunning && explorer.Containers.Count == 6, TimeSpan.FromSeconds(10));
            // The tree in the tool window; the Containers table as a document.
            explorer.Open(explorer.Sections[0]);
            await WaitUntil(() => explorer.Containers.AllRows.Count(r => r.Cpu is not null) == 4, TimeSpan.FromSeconds(10));
            await Settle();
            Save(window, "docker-explorer");

            // A container's page, on its stats.
            var api = explorer.Containers.AllRows.Single(r => r.Name == "shop-api-1");
            api.OpenTabCommand.Execute("stats");
            await WaitUntil(() => api.CpuHistory.Count >= 12, TimeSpan.FromSeconds(15));
            await Settle();
            Save(window, "docker-container");
            module.Dispose();
            await CloseAsync(vm, window);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KARVON_DOCKER_PATH", engine);
            File.Delete(compose);
        }
    }

    [AvaloniaFact]
    public async Task Installer()
    {
        Assert.SkipWhen(OutputDirectory is null, "Set KARVON_SCREENSHOTS to the output folder to render the README screenshots.");
        Directory.CreateDirectory(OutputDirectory!);
        var (updater, dir) = InstallerTests.UpdaterWithRelease("""
            ## What's new

            - **Karvon Studio Installer**: workloads and individual components, as Visual Studio Installer has them.
            - **Updates**: checked once a day, downloaded and checked, installed as Karvon Studio closes.
            - **Kotlin**: kotlin-lsp in the editor, F5 and Gradle's tests in Test Explorer.
            """);
        // A demo user's data folder (Karvon's components in it, none installed), as the other screenshots' demo paths.
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/home/dev/.local/share");
        var vm = NewViewModel();
        vm.Updates = updater;
        vm.Settings.DisabledModules = ["kotlin"];
        var installer = new InstallerViewModel(vm, updater);
        var window = new InstallerWindow { DataContext = installer, Width = 1180, Height = 720 };
        window.Show();
        try
        {
            await installer.CheckForUpdatesCommand.ExecuteAsync(null);
            await Settle();
            Save(window, "installer");

            installer.ShowModifyCommand.Execute(null);
            installer.Workloads[0].Items.Single(i => i.Name == "Kotlin").IsSelected = true;
            await Settle();
            Save(window, "installer-modify");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            vm.Settings.DisabledModules = [];
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Start_window()
    {
        Assert.SkipWhen(OutputDirectory is null, "Set KARVON_SCREENSHOTS to the output folder to render the README screenshots.");
        Directory.CreateDirectory(OutputDirectory!);
        // Display only: the start window lists recent entries without opening them.
        const string repos = "/home/dev/source/repos";
        var now = DateTime.Now;
        var settings = new Karvon.Services.Settings { NewProjectLocation = repos };
        string[] recent = [$"{repos}/TodoApp/TodoApp.sln", $"{repos}/Shop/Shop.slnx", $"{repos}/Inventory/Inventory.sln", $"{repos}/Api/Api.csproj"];
        settings.RecentSolutions.AddRange(recent);
        settings.RecentOpened[recent[0]] = now.AddMinutes(-20);
        settings.RecentOpened[recent[1]] = now.AddHours(-3);
        settings.RecentOpened[recent[2]] = now.AddDays(-2);
        settings.PinnedSolutions.Add(recent[3]);

        var vm = new StartViewModel(settings, templates: [new(".NET", Karvon.DotNet.Templates.TemplateCatalog.LoadAsync)]);
        var window = new StartWindow { DataContext = vm, Width = 1060, Height = 680 };
        window.Show();
        await Settle();
        Save(window, "start-window");

        vm.ShowNewProjectCommand.Execute(null);
        await WaitUntil(() => !vm.TemplatesLoading && vm.Templates.Count > 0, TimeSpan.FromMinutes(1));
        vm.SelectedTemplate = vm.Templates.FirstOrDefault(t => t.Name == "Blazor Web App") ?? vm.Templates[0];
        await Settle();
        Save(window, "new-project");
        window.Close();
    }
}
