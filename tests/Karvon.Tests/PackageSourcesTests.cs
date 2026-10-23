using Karvon.Go;
using Karvon.Java;
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

    static Task<(int Code, string Output)> RunAsync(PackageChange change) => RunAsync(change.Command!);

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
        Assert.Equal(["add", "rand@0.8.5", "--dev"], crates.Add("rand", "0.8.5", "dev-dependencies").Command!.Arguments);
        Assert.Equal(["add", "rand@0.8.5"], crates.Add("rand", "0.8.5", "dependencies").Command!.Arguments);
        Assert.Equal(["remove", "proptest", "--dev"], crates.Remove(crates.Dependencies()[3]).Command!.Arguments);
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
        // Its .venv as the platform lays it out: bin and lib/python3.x on Linux and macOS, Scripts and Lib on Windows.
        var site = OperatingSystem.IsWindows() ? Path.Combine(dir, ".venv", "Lib", "site-packages") : Path.Combine(dir, ".venv", "lib", "python3.12", "site-packages");
        Directory.CreateDirectory(Path.Combine(site, "requests-2.32.3.dist-info"));
        Directory.CreateDirectory(Path.Combine(site, "typing_extensions-4.12.2.dist-info"));
        var venvPython = OperatingSystem.IsWindows() ? Path.Combine(dir, ".venv", "Scripts", "python.exe") : Path.Combine(dir, ".venv", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(venvPython)!);
        File.WriteAllText(venvPython, "");
        var uv = new PypiPackages(dir);
        Assert.Equal(("uv", "shop"), (uv.Name, uv.Project));
        Assert.Equal(["dependencies", "dev"], uv.Groups);
        Assert.Equal([
            new DependencyPackage("requests", ">=2.31", "2.32.3", "dependencies"),
            new DependencyPackage("Typing_Extensions", null, "4.12.2", "dependencies"),
            new DependencyPackage("rich", null, null, "dependencies"),
            new DependencyPackage("pytest", ">=8", null, "dev"),
        ], uv.Dependencies());
        Assert.Equal(["add", "httpx==0.27.0", "--group", "dev"], uv.Add("httpx", "0.27.0", "dev").Command!.Arguments);
        Assert.Equal(["remove", "pytest", "--group", "dev"], uv.Remove(uv.Dependencies()[3]).Command!.Arguments);

        // No project of its own: its requirements.txt, pip into its virtual environment.
        File.Delete(Path.Combine(dir, "pyproject.toml"));
        File.WriteAllText(Path.Combine(dir, "requirements.txt"), "# pinned\nrequests==2.32.3\n-r more.txt\nflask\n");
        var pip = new PypiPackages(dir);
        Assert.Equal(("pip", 0), (pip.Name, pip.Groups.Count));
        Assert.Equal(["requests", "flask"], pip.Dependencies().Select(d => d.Id));
        var install = pip.Add("flask", "3.0.3", null);
        Assert.Equal(venvPython, install.Command!.Program);
        Assert.Equal(["-m", "pip", "install", "flask==3.0.3"], install.Command.Arguments);

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
        Assert.Equal(["get", "github.com/google/uuid@v1.5.0"], go.Add("github.com/google/uuid", "v1.5.0", null).Command!.Arguments);
        Assert.Equal(["get", "golang.org/x/text@none"], go.Remove(go.Dependencies()[1]).Command!.Arguments);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_maven_projects_pom_is_edited_as_it_is_written()
    {
        var dir = NewFolder();
        var pom = Path.Combine(dir, "pom.xml");
        File.WriteAllText(pom, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- The shop. -->
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>shop</artifactId>
              <properties>
                <junit.version>5.11.0</junit.version>
              </properties>
            </project>

            """);
        var maven = new MavenPackages(dir);
        Assert.Equal(("maven", "shop", "Maven Central"), (maven.Name, maven.Project, maven.Registry));
        Assert.Empty(maven.Dependencies());
        var ct = TestContext.Current.CancellationToken;

        // Added where there were none: a dependencies element, indented as the file is.
        Assert.Equal("com.google.guava:guava 33.4.0-jre added to pom.xml", await maven.Add("com.google.guava:guava", "33.4.0-jre", "compile").Edit!(ct));
        Assert.Equal("org.junit.jupiter:junit-jupiter 5.11.0 added to pom.xml (test)", await maven.Add("org.junit.jupiter:junit-jupiter", "5.11.0", "test").Edit!(ct));
        Assert.Equal("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- The shop. -->
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>shop</artifactId>
              <properties>
                <junit.version>5.11.0</junit.version>
              </properties>
              <dependencies>
                <dependency>
                  <groupId>com.google.guava</groupId>
                  <artifactId>guava</artifactId>
                  <version>33.4.0-jre</version>
                </dependency>
                <dependency>
                  <groupId>org.junit.jupiter</groupId>
                  <artifactId>junit-jupiter</artifactId>
                  <version>5.11.0</version>
                  <scope>test</scope>
                </dependency>
              </dependencies>
            </project>

            """, File.ReadAllText(pom));
        Assert.Equal([new DependencyPackage("com.google.guava:guava", "33.4.0-jre", "33.4.0-jre", "compile"), new DependencyPackage("org.junit.jupiter:junit-jupiter", "5.11.0", "5.11.0", "test")],
            maven.Dependencies());

        // Another version, then removed; a version from a property is read as it is.
        await maven.Add("com.google.guava:guava", "33.7.2-jre", "compile").Edit!(ct);
        Assert.Contains("<version>33.7.2-jre</version>", File.ReadAllText(pom));
        await maven.Remove(maven.Dependencies()[0]).Edit!(ct);
        Assert.DoesNotContain("guava", File.ReadAllText(pom));
        Assert.Contains("  <dependencies>\n    <dependency>\n      <groupId>org.junit.jupiter</groupId>", File.ReadAllText(pom));
        File.WriteAllText(pom, File.ReadAllText(pom).Replace("<version>5.11.0</version>", "<version>${junit.version}</version>"));
        Assert.Equal("5.11.0", Assert.Single(maven.Dependencies()).Version);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_gradle_projects_build_file_is_edited_as_it_is_written()
    {
        var dir = NewFolder();
        var build = Path.Combine(dir, "build.gradle.kts");
        File.WriteAllText(build, "plugins {\n    java\n}\n\ndependencies {\n    testImplementation(\"org.junit.jupiter:junit-jupiter:5.11.0\")\n}\n");
        var gradle = new MavenPackages(dir);
        Assert.Equal("gradle", gradle.Name);
        Assert.Equal([new DependencyPackage("org.junit.jupiter:junit-jupiter", "5.11.0", "5.11.0", "testImplementation")], gradle.Dependencies());
        var ct = TestContext.Current.CancellationToken;
        await gradle.Add("com.google.guava:guava", "33.4.0-jre", "implementation").Edit!(ct);
        await gradle.Add("org.junit.jupiter:junit-jupiter", "5.12.0", "testImplementation").Edit!(ct);
        Assert.Equal("plugins {\n    java\n}\n\ndependencies {\n    testImplementation(\"org.junit.jupiter:junit-jupiter:5.12.0\")\n    implementation(\"com.google.guava:guava:33.4.0-jre\")\n}\n",
            File.ReadAllText(build));
        await gradle.Remove(gradle.Dependencies()[0]).Edit!(ct);
        Assert.Equal("com.google.guava:guava", Assert.Single(gradle.Dependencies()).Id);

        // Groovy's form.
        File.Delete(build);
        File.WriteAllText(Path.Combine(dir, "build.gradle"), "dependencies {\n  implementation 'com.google.guava:guava:33.4.0-jre'\n}\n");
        await new MavenPackages(dir).Add("org.slf4j:slf4j-api", "2.0.16", "implementation").Edit!(ct);
        Assert.Equal("dependencies {\n  implementation 'com.google.guava:guava:33.4.0-jre'\n  implementation 'org.slf4j:slf4j-api:2.0.16'\n}\n", File.ReadAllText(Path.Combine(dir, "build.gradle")));

        // Maven's order of versions.
        Assert.False(MavenPackages.IsPrerelease("33.4.0-jre"));
        Assert.True(MavenPackages.IsPrerelease("2.0.0-rc1") && MavenPackages.IsPrerelease("6.0.0-M2") && MavenPackages.IsPrerelease("1.0-SNAPSHOT"));
        Assert.True(MavenPackages.CompareVersions("33.7.2-jre", "33.4.0-jre") > 0);
        Assert.True(MavenPackages.CompareVersions("1.10", "1.9") > 0);
        Assert.True(MavenPackages.CompareVersions("1.0-rc1", "1.0") < 0);
        Assert.True(MavenPackages.CompareVersions("1.0", "1.0.0") == 0);
        Assert.True(MavenPackages.CompareVersions("1.0.1", "1.0-rc1") > 0);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Maven_central_is_searched_and_its_versions_read()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_JAVA_LIVE") == "1", "Set KARVON_JAVA_LIVE=1 to search Maven Central.");
        var maven = new MavenPackages(NewFolder());
        var ct = TestContext.Current.CancellationToken;
        Assert.Contains(await maven.SearchAsync("guava", ct), p => p.Id == "com.google.guava:guava");
        var guava = (await maven.FindAsync("com.google.guava:guava", ct))!;
        Assert.Contains("Apache", guava.License);
        Assert.False(MavenPackages.IsPrerelease(guava.LatestVersion!));
        var versions = await maven.VersionsAsync("com.google.guava:guava", false, ct);
        Assert.Contains("33.4.0-jre", versions);
        Assert.Equal(guava.LatestVersion, versions[0]);
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
