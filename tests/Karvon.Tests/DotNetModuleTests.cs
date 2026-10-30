using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Build;
using Karvon.DotNet;
using Karvon.DotNet.Debugging;
using Karvon.Modules;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

[Collection(SampleSolutionCollection.Name)]
public class DotNetModuleTests
{
    /// <summary>A menu as its headers, "-" for a separator.</summary>
    static List<string> Lines(IEnumerable<object> items) =>
        [.. items.Select(i => i is MenuItem { Header: string header } ? header : i is Separator ? "-" : "?")];

    /// <summary>A menu's lines from one header to another, both in.</summary>
    static List<string> Between(IEnumerable<object> items, string first, string last)
    {
        var lines = Lines(items);
        var start = lines.IndexOf(first);
        return start < 0 ? [] : lines[start..(lines.IndexOf(last, start) + 1)];
    }

    [Fact]
    public void A_folders_own_dotnet_is_its_default_and_one_deeper_is_not()
    {
        var root = Path.Combine(Path.GetTempPath(), "karvon-dotnet-folder-" + Guid.NewGuid().ToString("N"));
        const string App = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
        // A backend a folder down (a repository with its frontend beside it): the folder's build and run.
        Directory.CreateDirectory(Path.Combine(root, "Shop.Backend"));
        File.WriteAllText(Path.Combine(root, "Shop.Backend", "Shop.Backend.csproj"), App);
        var own = DotNetFolder.Tasks(root);
        Assert.True(own.Single(t => t.Name == "dotnet: build").IsDefault);
        Assert.True(own.Single(t => t.Name == "dotnet: run - Shop.Backend").IsDefault);

        // A sample two folders down (in a Rust repository): its tasks are there, the folder's defaults are its own language's.
        Directory.Delete(Path.Combine(root, "Shop.Backend"), recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "samples", "app"));
        File.WriteAllText(Path.Combine(root, "samples", "app", "app.csproj"), App);
        var deep = DotNetFolder.Tasks(root);
        Assert.Equal(["dotnet: build", "dotnet: run - app"], deep.Select(t => t.Name));
        Assert.DoesNotContain(deep, t => t.IsDefault);
        Assert.True(DotNetFolder.Has(root));
        Directory.Delete(root, recursive: true);
        Assert.Empty(DotNetFolder.Tasks(Path.GetTempPath() + "karvon-missing-" + Guid.NewGuid().ToString("N")));
    }

    [AvaloniaFact]
    public async Task A_folder_with_projects_and_no_solution_builds_runs_debugs_and_tests_them()
    {
        // The sample's projects without their solution, opened as a folder, as a repository with a backend in it is.
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        File.Delete(sln);
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var settings = Path.Combine(dir, "Web", "Properties", "launchSettings.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("5280", port.ToString()));
        }
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        Assert.True(vm.IsFolderWorkspace);

        // Its tasks: the build (each project's, one after another), the tests', and the app's run, which is what starts.
        Assert.Equal(["dotnet: build", "dotnet: build - Components", "dotnet: build - Tests", "dotnet: build - Web", "dotnet: test - Tests", "dotnet: run - Web"],
            vm.WorkspaceTasks.Select(t => t.Name));
        Assert.Equal("dotnet: build", vm.BuildTask?.Name);
        Assert.True(vm.WorkspaceTasks.Single(t => t.Name == "dotnet: build").DependsInSequence);
        Assert.Equal("dotnet: run - Web", vm.StartupItem?.Name);
        Assert.Equal(["run", "--project", Path.Combine(dir, "Web", "Web.csproj"), "--launch-profile", "http"], vm.StartupItem!.Task!.Arguments);

        // Roslyn loads its projects: C# in the editor.
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(3));
        Assert.Equal("IntelliSense: ready", vm.ServerStatus);
        var program = Path.Combine(dir, "Web", "Program.cs");
        var doc = vm.OpenDocument(program)!;
        await WaitUntil(() => doc.Host.HasLanguageServer, TimeSpan.FromSeconds(30));
        Assert.True(doc.Host.HasLanguageServer);

        // Test Explorer has its tests.
        Assert.True(vm.HasTestFrameworksForClaude);

        // F5: the build, then the app under netcoredbg, stopped at its breakpoint.
        vm.Breakpoints.Toggle(program, 2);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(4));
        Assert.True(vm.Mode == DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.BuildOutput.Document.Text[^Math.Min(3000, vm.BuildOutput.Document.TextLength)..]);
        Assert.Equal((program, 2), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(60));
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        await TestTools.DeleteFolderAsync(dir);
    }

    [AvaloniaFact]
    public async Task Solution_explorer_and_tools_have_its_nuget_commands_where_visual_studio_has_them()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        ExplorerNode Node(params string[] names) => names.Aggregate(vm.ExplorerRoots[0], (node, name) =>
        {
            node.IsExpanded = true;
            return node.Children.Single(c => c.Name == name);
        });

        Assert.Equal(["Build Solution", "Rebuild Solution", "Clean Solution", "Run Tests", "-", "Manage NuGet Packages for Solution...", "Restore NuGet Packages", "-", "Add"],
            Between(window.ExplorerMenuItems(vm.ExplorerRoots[0]), "Build Solution", "Add"));
        Assert.Equal(["Build", "Rebuild", "Clean", "Pack", "Publish to Folder", "-", "Add", "Manage NuGet Packages...", "Manage User Secrets", "Set as Startup Project"],
            Between(window.ExplorerMenuItems(Node("Web")), "Build", "Set as Startup Project"));
        Assert.Equal(["Add Project Reference...", "Manage NuGet Packages..."], Lines(window.ExplorerMenuItems(Node("Tests", "Dependencies"))));
        var xunit = Node("Tests", "Dependencies", "Packages", "xunit");
        Assert.Equal(["Manage NuGet Packages...", "-", "Remove"], Lines(window.ExplorerMenuItems(xunit)));
        Assert.All(window.ExplorerMenuItems(xunit).OfType<MenuItem>(), item => Assert.True(item.IsEnabled));
        Assert.Equal(vm.Solution!.Projects.Single(p => p.Name == "Tests"), vm.ProjectOf(xunit));
        // Add > Class... and Razor Component... in a project and its folders, after Karvon's own items; C#'s templates first in Add New Item.
        var add = window.ExplorerMenuItems(Node("Web")).OfType<MenuItem>().Single(m => (string?)m.Header == "Add");
        Assert.Equal(["New Item...", "Existing Item...", "New Folder", "New EditorConfig", "-", "Project Reference...", "-", "Class...", "Razor Component..."],
            Lines(add.Items.OfType<object>()));
        Assert.Equal("Class", vm.NewItemTemplates[0].Name);
        Assert.Equal(["Class", "Interface", "Record", "Struct", "Enum", "Razor Component", "Code File", "JSON File", "Text File"], vm.NewItemTemplates.Take(9).Select(t => t.Name));
        // Tools > NuGet Package Manager first, as in Visual Studio.
        var tools = window.MainMenu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Tools");
        Assert.Equal(["_NuGet Package Manager", "-", "_Restart Language Server", "_Trust ASP.NET Core Development Certificate", "_ESLint in This Folder...", "_Settings Sync", "Inline _Completions...", "_Vault", "-", "_Get Tools and Features..."],
            Lines(tools.Items.OfType<object>()));
        Assert.Equal(["Manage NuGet Packages for _Solution..."], Lines(((MenuItem)tools.Items[0]!).Items.OfType<object>()));
        Assert.Contains(vm.OutputChannels, c => c.Name == "Package Manager");
        // Test Explorer has the solution's xUnit tests through the module.
        Assert.True(vm.RunAllTestsCommand.CanExecute(null));

        // Build, Rebuild, Clean, Pack and Publish are its dotnet build; a verb it has not is none of its.
        var build = vm.Module<DotNetModule>()!.BuildSolution(vm.Solution!, new BuildRequest("rebuild", sln, "Release"))!;
        Assert.Equal(DotnetCli.Executable, build.Program);
        Assert.Equal(["build", sln, "-c", "Release", "-nologo", "-v:minimal", "-clp:NoSummary;ForceNoAlign", "--no-incremental"], build.Arguments);
        Assert.True(build.ProjectBuilt!("  Web -> /out/Web.dll"));
        Assert.Null(vm.Module<DotNetModule>()!.BuildSolution(vm.Solution!, new BuildRequest("deploy", sln, "Debug")));

        // Remove on a package: out of the project file, and out of Dependencies; what dotnet said in Output (Package Manager).
        var dotnet = vm.Module<DotNetModule>()!;
        var tests = vm.Solution!.Projects.Single(p => p.Name == "Tests");
        Assert.True(await dotnet.RemovePackageAsync(tests, "xunit"), vm.StatusText);
        Assert.DoesNotContain("\"xunit\"", File.ReadAllText(tests.FilePath));
        Assert.DoesNotContain(vm.Solution!.Projects.Single(p => p.Name == "Tests").Packages, p => p.Name == "xunit");
        var output = vm.OutputChannels.Single(c => c.Name == "Package Manager");
        await WaitUntil(() => output.Document.Text.Contains("> dotnet remove"), TimeSpan.FromSeconds(5));
        Assert.Contains("> dotnet remove", output.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
    }

    [AvaloniaFact]
    public async Task New_project_has_the_sdks_templates_first_and_add_new_project_only_theirs()
    {
        var vm = NewViewModel();
        var start = new StartViewModel(new Settings(), templates: vm.ProjectTemplateSources);
        await start.EnsureTemplatesAsync();
        Assert.Null(start.Message);
        Assert.Equal("console", start.Templates[0].Template.ShortName);
        Assert.True(start.Templates[0].Template.MakesSolution);
        Assert.Contains(start.Templates, t => t.Template.Identity == "karvon.cpp.cmake");

        var adding = new StartViewModel(new Settings(), templates: vm.ProjectTemplateSources, addToSolution: "/src/Shop/Shop.sln");
        await adding.EnsureTemplatesAsync();
        Assert.Contains(adding.Templates, t => t.Template.ShortName == "classlib");
        Assert.All(adding.Templates, t => Assert.NotNull(t.Template.AddToSolution));

        // No module with templates on: the wizard says where they went.
        var none = new StartViewModel(new Settings(), templates: []);
        await none.EnsureTemplatesAsync();
        Assert.Empty(none.Templates);
        Assert.Equal("No project templates: the modules that have them are off (Tools > Get Tools and Features).", none.Message);
    }

    /// <summary>A console project in a folder of its own, restored from nuget.org only.</summary>
    static string NewConsoleProject(string launchSettings)
    {
        var dir = Directory.CreateTempSubdirectory("karvon-dotnet-launch-").FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample", "nuget.config"), Path.Combine(dir, "nuget.config"));
        File.WriteAllText(Path.Combine(dir, "Shop.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>Shop.App</AssemblyName></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "System.Console.WriteLine(\"hi\");");
        Directory.CreateDirectory(Path.Combine(dir, "Properties"));
        File.WriteAllText(Path.Combine(dir, "Properties", "launchSettings.json"), launchSettings);
        return Path.Combine(dir, "Shop.csproj");
    }

    [AvaloniaFact]
    public async Task A_dotnet_configuration_is_the_projects_assembly_with_its_launch_profile()
    {
        var project = NewConsoleProject("""
            { "profiles": { "https": { "commandName": "Project", "launchBrowser": true, "launchUrl": "swagger",
              "applicationUrl": "https://localhost:7001;http://localhost:5001", "commandLineArgs": "--seed 3",
              "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" } } } }
            """);
        var dir = Path.GetDirectoryName(project)!;
        var vm = NewViewModel();
        JsonObject Config(string profile) => JsonNode.Parse($$"""
            { "type": "dotnet", "request": "launch", "projectPath": {{JsonValue.Create(project).ToJsonString()}}, "launchProfile": "{{profile}}",
              "args": ["--fast"], "env": { "SHOP": "1" } }
            """)!.AsObject();
        var log = new List<string>();

        // Not built yet: it says so.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DotNetDebugger.ResolveAsync(Config("https"), vm, log.Add));
        Assert.Contains("build it first", error.Message);

        using (var build = ProcessRunner.Start(DotnetCli.Executable, ["build", project, "-nologo", "-v:quiet"], dir, _ => { }))
            Assert.Equal(0, await build.WaitAsync());
        var config = Config("https");
        await DotNetDebugger.ResolveAsync(config, vm, log.Add);
        Assert.Equal("coreclr", config["type"]!.GetValue<string>());
        Assert.Equal(DotnetCli.Executable, config["program"]!.GetValue<string>());
        var args = config["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.EndsWith("Shop.App.dll", args[0]);
        Assert.Equal(["--seed", "3", "--fast"], args.Skip(1));
        Assert.Equal(dir, config["cwd"]!.GetValue<string>());
        var env = config["env"]!.AsObject();
        Assert.Equal("https://localhost:7001;http://localhost:5001", env["ASPNETCORE_URLS"]!.GetValue<string>());
        Assert.Equal("Development", env["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Equal("1", env["SHOP"]!.GetValue<string>());
        // launchBrowser: the browser opens at the launchUrl of where the app listens.
        var ready = Karvon.Debugging.ServerReadyAction.Of(config, log.Add)!;
        Assert.Equal("https://localhost:7001/swagger", ready.Match("info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: https://localhost:7001\n"));

        // Ctrl+F5 of a console app: dotnet run of what the build made in its console, with the launch profile, the
        // browser opening where it listens. No Hot Reload: dotnet watch would take its keyboard.
        var debugger = vm.Module<DotNetModule>()!.Debuggers.Single();
        var run = (await debugger.RunWithoutDebuggingAsync!(Config("https")))!;
        Assert.Equal(("Shop", DotnetCli.Executable, dir), (run.Name, run.Program, run.WorkingDirectory));
        Assert.Equal(["run", "--project", project, "-c", "Debug", "--no-build", "--launch-profile", "https"], run.Arguments);
        Assert.Equal("%s/swagger", run.ServerReadyAction!["uriFormat"]!.GetValue<string>());
        Assert.True(run.RunsInConsole);
        Assert.False(run.HotReloads);
        // Its output in Output: dotnet watch of the project (Hot Reload).
        var internalConsole = Config("https");
        internalConsole["console"] = "internalConsole";
        run = (await debugger.RunWithoutDebuggingAsync!(internalConsole))!;
        Assert.Equal(["watch", "--project", project, "--non-interactive", "-c", "Debug", "-v", "q", "--launch-profile", "https"], run.Arguments);
        Assert.True(run.HotReloads);
        Assert.NotNull(run.OutputReader);
        Assert.Equal("1", run.Environment!["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"]);
        Assert.Equal("%s/swagger", run.ServerReadyAction!["uriFormat"]!.GetValue<string>());
        // Without Hot Reload: dotnet run.
        vm.Settings.HotReload = false;
        run = (await debugger.RunWithoutDebuggingAsync!(internalConsole))!;
        Assert.Equal(["run", "--project", project, "-c", "Debug", "--no-build", "--launch-profile", "https"], run.Arguments);
        Assert.False(run.HotReloads);
        Assert.Null(run.OutputReader);
        vm.Settings.HotReload = true;
        Assert.Null(await debugger.RunWithoutDebuggingAsync!(new JsonObject { ["type"] = "coreclr", ["program"] = "app.dll" }));

        // A profile the project does not have: it starts without one, and says so.
        var none = Config("Docker");
        await DotNetDebugger.ResolveAsync(none, vm, log.Add);
        Assert.Null(none["serverReadyAction"]);
        Assert.Null(none["env"]!["ASPNETCORE_URLS"]);
        Assert.Contains(log, l => l.Contains("no launch profile 'Docker'"));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Roslyn_starts_with_the_solution_and_again_on_restart()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var program = vm.OpenDocument(SampleSolution.PathOf(sln, "Web/Program.cs"))!;
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));
        var first = Assert.Single(vm.ModuleLanguageServers);
        Assert.Same(first, vm.LanguageServerFor(program.FilePath));

        // Tools > Restart Language Server: a new Roslyn, for the files open already.
        await vm.RestartLanguageServerCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ServerStatus == "IntelliSense: ready", TimeSpan.FromMinutes(5));
        Assert.Equal("IntelliSense: ready", vm.ServerStatus);
        var second = Assert.Single(vm.ModuleLanguageServers);
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Same(second, vm.LanguageServerFor(program.FilePath));
        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.True(second.IsDisposed);
        Assert.Equal("", vm.ServerStatus);
    }

    [AvaloniaFact]
    public async Task With_the_module_off_a_solution_does_not_open_and_its_folder_is_a_folder()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel(settings => settings.DisabledModules = ["dotnet"]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Assert.DoesNotContain(vm.Modules, m => m.Id == "dotnet");
        Assert.Contains(vm.AllModules, m => m.Id == "dotnet" && m.Components.Count == 2);

        // No module opens a .sln: it says which module is off.
        Assert.False(vm.CanOpen(sln));
        await vm.OpenSolutionAsync(sln);
        Assert.Null(vm.Solution);
        Assert.Equal("Failed to open solution: no module opens BlazorSample.sln: its module is off (Tools > Get Tools and Features)", vm.StatusText);

        // Its folder opens as a folder: none of .NET's commands, templates, tests or language server.
        var dir = Path.GetDirectoryName(sln)!;
        Assert.True(vm.CanOpen(dir));
        await vm.OpenSolutionAsync(dir);
        Assert.True(vm.Solution!.IsFolder);
        var web = vm.ExplorerRoots[0].Children.Single(c => c.Name == "Web");
        Assert.Equal(NodeKind.Folder, web.Kind);
        var add = window.ExplorerMenuItems(web).OfType<MenuItem>().Single(m => (string?)m.Header == "Add");
        Assert.Equal(["New Item...", "Existing Item...", "New Folder"], Lines(add.Items.OfType<object>()));
        Assert.Equal(ItemTemplates.General, vm.NewItemTemplates);
        Assert.False(vm.RunAllTestsCommand.CanExecute(null));
        Assert.Empty(vm.ModuleLanguageServers);
        Assert.Equal("", vm.ServerStatus);
        var tools = window.MainMenu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Tools");
        Assert.Equal(["_Restart Language Server", "_ESLint in This Folder...", "_Settings Sync", "Inline _Completions...", "_Vault", "-", "_Get Tools and Features..."], Lines(tools.Items.OfType<object>()));
        Assert.True(await vm.CloseSolutionCoreAsync());
        // The tests' settings are shared: the module is on again for the next.
        vm.Settings.DisabledModules = [];
        vm.Settings.Save();
    }
}
