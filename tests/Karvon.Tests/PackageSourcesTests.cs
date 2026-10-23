using Karvon.Go;
using Karvon.Packages;
using Karvon.Python;
using Karvon.Rust;
using Karvon.Workspace;

namespace Karvon.Tests;

/// <summary>The languages' packages: what their manifests ask for, and their package managers' commands.</summary>
public class PackageSourcesTests
{
    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-packages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string ToolPath() => string.Join(Path.PathSeparator, new[]
    {
        Karvon.Build.NodeLocator.DirectoryOffPath(), RustTools.DirectoryOffPath(), Environment.GetEnvironmentVariable("PATH"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin"),
        "/usr/local/go/bin",
    }.OfType<string>());

    /// <summary>A package manager's command, run where it runs: its exit code and output.</summary>
    static async Task<(int Code, string Output)> RunAsync(Karvon.Modules.WorkspaceTask task)
    {
        var path = ToolPath();
        var program = TaskVariables.Find(task.Program, path, task.WorkingDirectory) ?? task.Program;
        var lines = new List<string>();
        using var runner = Karvon.Build.ProcessRunner.Start(program, task.Arguments, task.WorkingDirectory, lines.Add, new Dictionary<string, string> { ["PATH"] = path });
        var code = await runner.WaitAsync(TestContext.Current.CancellationToken);
        return (code, string.Join("\n", lines));
    }

    [Fact]
    public void Toml_reads_tables_keys_and_arrays()
    {
        var tables = Toml.Read("""
            # a comment
            [package]
            name = "shop" # its name
            [dependencies]
            serde = { version = "1.0", features = ["derive", "rc"] }
            [project]
            dependencies = [
              "requests>=2.31",  # the HTTP one
              'rich',
            ]
            [[package]]
            name = "a#b"
            """);
        Assert.Equal(["", "package", "dependencies", "project", "package"], tables.Select(t => t.Name));
        Assert.Equal("shop", Toml.Unquote(tables[1]["name"]!));
        Assert.Equal("1.0", Toml.InlineValue(tables[2]["serde"]!, "version"));
        Assert.Equal(["requests>=2.31", "rich"], Toml.Strings(tables[3]["dependencies"]!));
        Assert.Equal("a#b", Toml.Unquote(tables[4]["name"]!));
    }

    [Fact]
    public void A_cargo_packages_crates_and_its_commands()
    {
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "Cargo.toml"), """
            [package]
            name = "shop"
            version = "0.1.0"

            [dependencies]
            serde = { version = "1.0.200", features = ["derive"] }
            anyhow = "1"
            local = { path = "../local" }

            [dev-dependencies]
            proptest = "1.4"

            [dependencies.tokio]
            version = "1.38"
            features = ["full"]
            """);
        File.WriteAllText(Path.Combine(dir, "Cargo.lock"), """
            [[package]]
            name = "anyhow"
            version = "1.0.86"

            [[package]]
            name = "serde"
            version = "1.0.203"
            """);
        var crates = new CratesPackages(dir);
        Assert.Equal(("cargo", "crates.io", "shop"), (crates.Name, crates.Registry, crates.Project));
        Assert.Equal([
            new DependencyPackage("serde", "1.0.200", "1.0.203", "dependencies"),
            new DependencyPackage("anyhow", "1", "1.0.86", "dependencies"),
            new DependencyPackage("local", "path ../local", null, "dependencies"),
            new DependencyPackage("proptest", "1.4", null, "dev-dependencies"),
            new DependencyPackage("tokio", "1.38", null, "dependencies"),
        ], crates.Dependencies());
        Assert.Equal(["add", "rand@0.8.5", "--dev"], crates.Add("rand", "0.8.5", "dev-dependencies").Arguments);
        Assert.Equal(["add", "rand@0.8.5"], crates.Add("rand", "0.8.5", "dependencies").Arguments);
        Assert.Equal(["remove", "proptest", "--dev"], crates.Remove(crates.Dependencies()[3]).Arguments);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void A_python_projects_packages_by_uv_or_pip()
    {
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "pyproject.toml"), """
            [project]
            name = "shop"
            dependencies = ["requests>=2.31", "Typing_Extensions[foo] ; python_version < '3.12'", "rich"]

            [dependency-groups]
            dev = ["pytest>=8"]
            """);
        var site = Path.Combine(dir, ".venv", "lib", "python3.12", "site-packages");
        Directory.CreateDirectory(Path.Combine(site, "requests-2.32.3.dist-info"));
        Directory.CreateDirectory(Path.Combine(site, "typing_extensions-4.12.2.dist-info"));
        Directory.CreateDirectory(Path.Combine(dir, ".venv", "bin"));
        File.WriteAllText(Path.Combine(dir, ".venv", "bin", "python"), "");
        var uv = new PypiPackages(dir);
        Assert.Equal(("uv", "shop"), (uv.Name, uv.Project));
        Assert.Equal(["dependencies", "dev"], uv.Groups);
        Assert.Equal([
            new DependencyPackage("requests", ">=2.31", "2.32.3", "dependencies"),
            new DependencyPackage("Typing_Extensions", null, "4.12.2", "dependencies"),
            new DependencyPackage("rich", null, null, "dependencies"),
            new DependencyPackage("pytest", ">=8", null, "dev"),
        ], uv.Dependencies());
        Assert.Equal(["add", "httpx==0.27.0", "--group", "dev"], uv.Add("httpx", "0.27.0", "dev").Arguments);
        Assert.Equal(["remove", "pytest", "--group", "dev"], uv.Remove(uv.Dependencies()[3]).Arguments);

        // No project of its own: its requirements.txt, pip into its virtual environment.
        File.Delete(Path.Combine(dir, "pyproject.toml"));
        File.WriteAllText(Path.Combine(dir, "requirements.txt"), "# pinned\nrequests==2.32.3\n-r more.txt\nflask\n");
        var pip = new PypiPackages(dir);
        Assert.Equal(("pip", 0), (pip.Name, pip.Groups.Count));
        Assert.Equal(["requests", "flask"], pip.Dependencies().Select(d => d.Id));
        var install = pip.Add("flask", "3.0.3", null);
        Assert.Equal(Path.Combine(dir, ".venv", "bin", "python"), install.Program);
        Assert.Equal(["-m", "pip", "install", "flask==3.0.3"], install.Arguments);

        // PEP 440's order and prereleases.
        Assert.True(PypiPackages.CompareVersions("2.0.0rc1", "2.0.0") < 0);
        Assert.True(PypiPackages.CompareVersions("2.0.0.dev1", "2.0.0a1") < 0);
        Assert.True(PypiPackages.CompareVersions("2.0.0.post1", "2.0.0") > 0);
        Assert.True(PypiPackages.CompareVersions("2.10", "2.9") > 0);
        Assert.True(PypiPackages.IsPrerelease("3.0b2") && !PypiPackages.IsPrerelease("3.0.1"));
        Assert.Equal("typing-extensions", PypiPackages.Normalize("Typing_Extensions"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void A_go_modules_requirements_and_go_get()
    {
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "go.mod"), """
            module example.com/shop

            go 1.23

            require github.com/google/uuid v1.6.0

            require (
            	golang.org/x/text v0.16.0
            	rsc.io/quote v1.5.2 // indirect
            )
            """);
        var go = new GoPackages(dir);
        Assert.Equal(("go", "example.com/shop"), (go.Name, go.Project));
        Assert.Equal([new DependencyPackage("github.com/google/uuid", "v1.6.0", "v1.6.0", "require"), new DependencyPackage("golang.org/x/text", "v0.16.0", "v0.16.0", "require"),
            new DependencyPackage("rsc.io/quote", "v1.5.2", "v1.5.2", "indirect")], go.Dependencies());
        Assert.Equal(["get", "github.com/google/uuid@v1.5.0"], go.Add("github.com/google/uuid", "v1.5.0", null).Arguments);
        Assert.Equal(["get", "golang.org/x/text@none"], go.Remove(go.Dependencies()[1]).Arguments);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Crates_io_is_searched_and_cargo_adds_and_removes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_RUST_LIVE") == "1" && TaskVariables.Find("cargo", ToolPath(), "/") is not null,
            "Set KARVON_RUST_LIVE=1, with cargo installed, to search crates.io and add from it.");
        var dir = NewFolder();
        Assert.Equal(0, (await RunAsync(new("init", Karvon.Modules.WorkspaceTaskKind.Other, "cargo", ["init", "--name", "shop", "--vcs", "none"], dir))).Code);
        var crates = new CratesPackages(dir);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("itoa", (await crates.SearchAsync("itoa", ct))[0].Id);
        Assert.Equal("MIT OR Apache-2.0", (await crates.FindAsync("itoa", ct))!.License);
        Assert.Contains("1.0.11", await crates.VersionsAsync("itoa", false, ct));
        var (code, output) = await RunAsync(crates.Add("itoa", "1.0.11", "dependencies"));
        Assert.True(code == 0, output);
        Assert.Equal(("itoa", "1.0.11"), crates.Dependencies().Single() is var d ? (d.Id, d.Requested!) : default);
        (code, output) = await RunAsync(crates.Remove(crates.Dependencies().Single()));
        Assert.True(code == 0, output);
        Assert.Empty(crates.Dependencies());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Pypi_is_looked_up_and_uv_adds_and_removes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1" && TaskVariables.Find("uv", ToolPath(), "/") is not null,
            "Set KARVON_PYTHON_LIVE=1, with uv installed, to look up PyPI and add from it.");
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "pyproject.toml"), "[project]\nname = \"shop\"\nversion = \"0.1.0\"\nrequires-python = \">=3.9\"\ndependencies = []\n");
        var pypi = new PypiPackages(dir);
        var ct = TestContext.Current.CancellationToken;
        var found = Assert.Single(await pypi.SearchAsync("IniConfig", ct));
        Assert.Equal("iniconfig", found.Id);
        Assert.Contains("2.0.0", await pypi.VersionsAsync("iniconfig", false, ct));
        var (code, output) = await RunAsync(pypi.Add("iniconfig", "2.0.0", "dev"));
        Assert.True(code == 0, output);
        Assert.Equal(("iniconfig", "==2.0.0", "2.0.0", "dev"), pypi.Dependencies().Single() is var d ? (d.Id, d.Requested!, d.Version!, d.Group) : default);
        (code, output) = await RunAsync(pypi.Remove(pypi.Dependencies().Single()));
        Assert.True(code == 0, output);
        Assert.Empty(pypi.Dependencies());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Pkg_go_dev_is_searched_and_go_gets_and_drops()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_GO_LIVE") == "1" && TaskVariables.Find("go", ToolPath(), "/") is not null,
            "Set KARVON_GO_LIVE=1, with Go installed, to search pkg.go.dev and get from the module proxy.");
        var dir = NewFolder();
        Assert.Equal(0, (await RunAsync(new("init", Karvon.Modules.WorkspaceTaskKind.Other, "go", ["mod", "init", "example.com/shop"], dir))).Code);
        var go = new GoPackages(dir);
        var ct = TestContext.Current.CancellationToken;
        var uuid = (await go.SearchAsync("uuid", ct)).First(p => p.Id == "github.com/google/uuid");
        Assert.Equal("BSD-3-Clause", uuid.License);
        Assert.Contains("v1.6.0", await go.VersionsAsync("github.com/google/uuid", false, ct));
        var (code, output) = await RunAsync(go.Add("github.com/google/uuid", "v1.6.0", null));
        Assert.True(code == 0, output);
        // go get adds what nothing imports yet as indirect: it shows so, until code imports it.
        Assert.Equal(new DependencyPackage("github.com/google/uuid", "v1.6.0", "v1.6.0", "indirect"), Assert.Single(go.Dependencies()));
        (code, output) = await RunAsync(go.Remove(go.Dependencies().Single()));
        Assert.True(code == 0, output);
        Assert.DoesNotContain("github.com/google/uuid", File.ReadAllText(Path.Combine(dir, "go.mod")));
        Directory.Delete(dir, recursive: true);
    }
}
