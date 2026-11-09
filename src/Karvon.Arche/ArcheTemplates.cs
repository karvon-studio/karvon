using System.Text;
using Karvon.Templates;

namespace Karvon.Arche;

/// <summary>Arche's new packages: arche-driver has no new command, so their files are written here.</summary>
public static class ArcheTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.arche.program", "Arche program", "Arche", "A package with one program and a test (package.arche, build.arche, main.arche).", "archeapp", ["Console"], (request, _, ct) =>
            WriteAsync(request, ct, ("build.arche", BuildFile), ("main.arche", Main))),
        ProjectTemplate.OfModule("karvon.arche.library", "Arche library", "Arche", "A package of one module and a test, with nothing to run (package.arche, lib.arche).", "archelib", ["Library"], (request, _, ct) =>
            WriteAsync(request, ct, ("lib.arche", Library))),
    ];

    /// <summary>A package's name is what imports call it: an identifier, as the compiler holds it to.</summary>
    static string Manifest(string name) => $$"""
        import std.build;

        const PACKAGE: build.Package = build.Package {
            name: "{{TemplateSteps.PackageName(name, '_')}}",
            version: "0.1.0",
            edition: 2027,
            needs: [],
        };

        """;

    const string BuildFile = """
        import std.build;

        const BUILD: build.Build = build.Build {
            programs: [build.program("main", "main.arche")],
        };

        """;

    const string Main = """
        import std;

        /// What the program says.
        fn greeting() -> str {
            return "hello, world";
        }

        fn main(sys: &mut std.System) -> !void {
            sys.stdout.write(greeting());
            sys.stdout.write("\n");
        }

        test "the greeting says hello" {
            assert(greeting().eq("hello, world"));
        }

        """;

    const string Library = """
        /// Twice the number.
        pub fn twice(n: i32) -> i32 {
            return n * 2;
        }

        test "twice doubles" {
            assert(twice(21) == 42);
        }

        """;

    /// <summary>Arche's lines end with a newline on every system, which the raw strings above may not have from this file.</summary>
    static async Task<string> WriteAsync(NewProjectRequest request, CancellationToken ct, params (string Name, string Text)[] files)
    {
        var dir = request.ProjectDirectory;
        Directory.CreateDirectory(dir);
        var encoding = new UTF8Encoding(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "package.arche"), Manifest(request.ProjectName).ReplaceLineEndings("\n"), encoding, ct);
        foreach (var (name, text) in files)
            await File.WriteAllTextAsync(Path.Combine(dir, name), text.ReplaceLineEndings("\n"), encoding, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), "build/\nvendor/\n", encoding, ct);
        return dir;
    }
}
