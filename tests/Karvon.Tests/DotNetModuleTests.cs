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
        Assert.Equal(["_NuGet Package Manager", "-", "_Restart Language Server"], Lines(tools.Items.OfType<object>()).Take(3));
        Assert.Equal(["Manage NuGet Packages for _Solution..."], Lines(((MenuItem)tools.Items[0]!).Items.OfType<object>()));
        Assert.Contains(vm.OutputChannels, c => c.Name == "Package Manager");
        // Test Explorer has the solution's xUnit tests through the module.
        Assert.True(vm.RunAllTestsCommand.CanExecute(null));

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
        Assert.Equal("No project templates: the modules that have them are off (Tools > Modules).", none.Message);
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

        // A profile the project does not have: it starts without one, and says so.
        var none = Config("Docker");
        await DotNetDebugger.ResolveAsync(none, vm, log.Add);
        Assert.Null(none["serverReadyAction"]);
        Assert.Null(none["env"]!["ASPNETCORE_URLS"]);
        Assert.Contains(log, l => l.Contains("no launch profile 'Docker'"));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task With_the_module_off_there_is_no_nuget()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel(settings => settings.DisabledModules = ["dotnet"]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        Assert.Equal(["Build Solution", "Rebuild Solution", "Clean Solution", "Run Tests", "-", "Add"],
            Between(window.ExplorerMenuItems(vm.ExplorerRoots[0]), "Build Solution", "Add"));
        Assert.DoesNotContain(vm.Modules, m => m.Id == "dotnet");
        var web = vm.ExplorerRoots[0].Children.Single(c => c.Name == "Web");
        var add = window.ExplorerMenuItems(web).OfType<MenuItem>().Single(m => (string?)m.Header == "Add");
        Assert.Equal(["New Item...", "Existing Item...", "New Folder", "New EditorConfig", "-", "Project Reference..."], Lines(add.Items.OfType<object>()));
        Assert.Equal(ItemTemplates.General, vm.NewItemTemplates);
        Assert.False(vm.RunAllTestsCommand.CanExecute(null));
        Assert.Contains(vm.AllModules, m => m.Id == "dotnet" && m.Components.Count == 2);
        var tools = window.MainMenu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "_Tools");
        Assert.Equal("_Restart Language Server", ((MenuItem)tools.Items[0]!).Header);
        Assert.True(await vm.CloseSolutionCoreAsync());
    }
}
