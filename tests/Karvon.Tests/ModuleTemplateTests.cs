using Avalonia.Headless.XUnit;
using Karvon.Services;
using Karvon.Templates;
using Karvon.ViewModels;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The languages' new projects: made by their modules (their tools, or their files), built and tested as made.</summary>
public class ModuleTemplateTests
{
    static NewProjectRequest RequestFor(ProjectTemplate template, string name) =>
        new(template, name, Directory.CreateTempSubdirectory("karvon-new-").FullName, name, true, []);

    static async Task<string> CreateAsync(ProjectTemplate template, string name = "Shop App")
    {
        var request = RequestFor(template, name);
        Directory.CreateDirectory(request.ProjectDirectory);
        var log = new List<string>();
        var folder = await template.Create!(request, log.Add, TestContext.Current.CancellationToken);
        Assert.Equal(request.ProjectDirectory, folder);
        return folder;
    }

    static ProjectTemplate Template(string identity) =>
        Karvon.BuiltInModules.Create().SelectMany(m => m.ProjectTemplates).Single(t => t.Identity == identity);

    static bool Has(string tool) => TaskVariables.Find(tool, string.Join(Path.PathSeparator,
        Go.GoTools.PathWithGo(Rust.RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? ""))), "/") is not null;

    static async Task<int> RunAsync(string program, IReadOnlyList<string> args, string dir, List<string> output)
    {
        var path = string.Join(Path.PathSeparator, Go.GoTools.PathWithGo(Rust.RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? "")));
        using var runner = Karvon.Build.ProcessRunner.Start(TaskVariables.Find(program, path, dir)!, args, dir, output.Add, new Dictionary<string, string> { ["PATH"] = path });
        return await runner.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Every_language_module_brings_its_templates()
    {
        Assert.Equal(["karvon.typescript.node", "karvon.python.project", "karvon.python.uv", "karvon.go.module", "karvon.rust.bin", "karvon.rust.lib", "karvon.cpp.cmake", "karvon.c.cmake", "karvon.java.maven", "karvon.kotlin.gradle"],
            Karvon.BuiltInModules.Create().SelectMany(m => m.ProjectTemplates).Select(t => t.Identity));
        Assert.Equal("shop_app", TemplateSteps.PackageName("Shop App", '_'));
        Assert.Equal("app-1st", TemplateSteps.PackageName("1st", '-'));
    }

    [Fact]
    public async Task A_cmake_project_builds_and_tests_as_made()
    {
        var dir = await CreateAsync(Template("karvon.cpp.cmake"));
        Assert.Contains("add_executable(shop_app src/main.cpp)", File.ReadAllText(Path.Combine(dir, "CMakeLists.txt")));
        Assert.Equal(["cmake: build", "cmake: configure", "ctest", "cmake: run shop_app"], Cpp.CppTasks.Read(dir).Select(t => t.Name));
        if (Has("cmake"))
        {
            var output = new List<string>();
            Assert.True(await RunAsync("cmake", ["-S", ".", "-B", "build"], dir, output) == 0, string.Join("\n", output));
            Assert.True(await RunAsync("cmake", ["--build", "build"], dir, output) == 0, string.Join("\n", output));
            Assert.True(await RunAsync("ctest", [.. Cpp.CppTasks.CTestArguments], dir, output) == 0, string.Join("\n", output));
        }
        Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
    }

    [Fact]
    public async Task A_go_module_and_a_cargo_package_build_as_made()
    {
        Assert.SkipUnless(Has("go") && Has("cargo"), "Go and Rust are needed to make their projects.");
        var go = await CreateAsync(Template("karvon.go.module"));
        Assert.StartsWith("module shop-app", File.ReadAllText(Path.Combine(go, "go.mod")));
        var output = new List<string>();
        Assert.True(await RunAsync("go", ["build", "./..."], go, output) == 0, string.Join("\n", output));
        Assert.Contains("go: run", Go.GoTasks.Read(go).Select(t => t.Name));

        var rust = await CreateAsync(Template("karvon.rust.lib"));
        Assert.Contains("name = \"shop_app\"", File.ReadAllText(Path.Combine(rust, "Cargo.toml")));
        Assert.True(File.Exists(Path.Combine(rust, "src", "lib.rs")));
        Assert.True(await RunAsync("cargo", ["test", "--quiet"], rust, output) == 0, string.Join("\n", output));
        Directory.Delete(Path.GetDirectoryName(go)!, recursive: true);
        Directory.Delete(Path.GetDirectoryName(rust)!, recursive: true);
    }

    [Fact]
    public async Task A_java_project_is_maven_with_its_test()
    {
        var dir = await CreateAsync(Template("karvon.java.maven"));
        Assert.Contains("<artifactId>shop-app</artifactId>", File.ReadAllText(Path.Combine(dir, "pom.xml")));
        Assert.True(File.Exists(Path.Combine(dir, "src", "main", "java", "com", "example", "shop_app", "App.java")));
        Assert.True(File.Exists(Path.Combine(dir, "src", "test", "java", "com", "example", "shop_app", "AppTest.java")));
        Assert.Equal(["maven: compile", "maven: test", "maven: package", "java: run main"], Java.JavaTasks.Read(dir).Select(t => t.Name));
        Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
    }

    [Fact]
    public async Task A_python_project_has_its_venv_with_pytest()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1" && Has("python3"), "Set KARVON_PYTHON_LIVE=1, with Python installed, to make a .venv and install pytest in it.");
        var dir = await CreateAsync(Template("karvon.python.project"));
        Assert.NotNull(Python.PythonEnvironment.VirtualEnvironmentBin(dir));
        Assert.True(Python.PytestFramework.IsUsedIn(dir));
        var output = new List<string>();
        Assert.True(await RunAsync(Python.PythonEnvironment.Interpreter(dir, "")!, ["-m", "pytest", "-q"], dir, output) == 0, string.Join("\n", output));
        Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
    }

    [Fact]
    public async Task A_uv_project_syncs_and_tests_as_made()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1" && Has("uv"), "Set KARVON_PYTHON_LIVE=1, with uv installed, to make a uv project.");
        var dir = await CreateAsync(Template("karvon.python.uv"));
        Assert.True(Python.UvProject.Is(dir));
        Assert.True(File.Exists(Path.Combine(dir, "uv.lock")));
        Assert.Equal(["uv: sync", "uv: run shop-app"], Python.PythonTasks.Read(dir).Select(t => t.Name).Take(2));
        var output = new List<string>();
        Assert.True(await RunAsync("uv", ["run", "pytest", "-q"], dir, output) == 0, string.Join("\n", output));
        Assert.NotNull(Python.PythonEnvironment.VirtualEnvironmentBin(dir));
        Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
    }

    [Fact]
    public async Task A_typescript_project_builds_and_tests_as_made()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1" && Has("npm"), "Set KARVON_TYPESCRIPT_LIVE=1, with Node.js installed, to install the project's packages.");
        var dir = await CreateAsync(Template("karvon.typescript.node"));
        var output = new List<string>();
        Assert.True(await RunAsync("npm", ["run", "build"], dir, output) == 0, string.Join("\n", output));
        Assert.True(File.Exists(Path.Combine(dir, "dist", "main.js")));
        Assert.True(await RunAsync("npm", ["test"], dir, output) == 0, string.Join("\n", output));
        Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
    }

    /// <summary>The language modules' templates, without .NET's (the SDK's are StartWindowTests').</summary>
    static IReadOnlyList<ProjectTemplateSource> Sources(IEnumerable<Karvon.Modules.KarvonModule> modules) =>
        [.. modules.Where(m => m.Id != "dotnet").Select(m => new ProjectTemplateSource(m.Name, m.LoadProjectTemplatesAsync))];

    [AvaloniaFact]
    public async Task New_project_makes_a_modules_project_as_a_folder()
    {
        var location = Directory.CreateTempSubdirectory("karvon-new-").FullName;
        var settings = new Settings { NewProjectLocation = location };
        var modules = Karvon.BuiltInModules.Create();
        var vm = new StartViewModel(settings, templates: Sources(modules));
        string? chosen = null;
        vm.SolutionChosen += path => chosen = path;
        await vm.EnsureTemplatesAsync();
        Assert.Contains("C++", vm.Languages);
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Identity == "karvon.cpp.cmake");
        // A module's project has no solution around it.
        Assert.False(vm.IsCreatingSolution);
        vm.NextCommand.Execute(null);
        vm.ProjectName = "shapes";
        Assert.Equal($"Project will be created in \"{Path.Combine(location, "shapes")}{Path.DirectorySeparatorChar}\"", vm.CreatedInText);
        vm.NextCommand.Execute(null);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(location, "shapes"), chosen);
        Assert.True(File.Exists(Path.Combine(location, "shapes", "CMakeLists.txt")));

        // The same name again: the folder is there already.
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Identity == "karvon.cpp.cmake");
        vm.ProjectName = "shapes";
        Assert.Contains("is there already", vm.ConfigureError);

        // Add > New Project to a solution has only the templates that add to one (.NET's).
        var adding = new StartViewModel(new Settings(), templates: Sources(modules), addToSolution: Path.Combine(location, "x.sln"));
        await adding.EnsureTemplatesAsync();
        Assert.Empty(adding.Templates);
        Directory.Delete(location, recursive: true);
    }
}
