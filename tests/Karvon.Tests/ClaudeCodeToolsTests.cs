using Avalonia.Headless.XUnit;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Claude's code navigation through Roslyn: declarations by name, a file's outline, a declaration's source, its definition, references and implementations.</summary>
public class ClaudeCodeToolsTests
{
    [AvaloniaFact]
    public async Task Claude_finds_and_reads_code_by_what_it_means()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-code-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            Directory.CreateDirectory(Path.Combine(dir, "Shapes"));
            File.WriteAllText(Path.Combine(dir, "Shapes", "IShape.cs"), """
                namespace Geometry;

                /// <summary>A shape with an area.</summary>
                public interface IShape
                {
                    double Area();
                }
                """);
            File.WriteAllText(Path.Combine(dir, "Shapes", "Circle.cs"), """
                namespace Geometry;

                public sealed class Circle(double radius) : IShape
                {
                    /// <summary>πr².</summary>
                    public double Area() => Math.PI * radius * radius;
                }
                """);
            File.WriteAllText(Path.Combine(dir, "Shapes", "Square.cs"), """
                namespace Geometry;

                public sealed class Square(double side) : IShape
                {
                    public double Area() => side * side;

                    public double Area(int scale) => Area() * scale;
                }
                """);
            File.WriteAllText(Path.Combine(dir, "Program.cs"), """
                using Geometry;

                var shapes = new IShape[] { new Circle(1), new Square(2) };
                Console.WriteLine(Report.Total(shapes));

                static class Report
                {
                    public static double Total(IEnumerable<IShape> shapes) => shapes.Sum(s => s.Area());
                }
                """);
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(Path.Combine(dir, "app.csproj"));
            var server = ClaudeOf(vm).ToolServer();
            Task<string> Text(string tool, object arguments) => ClaudeIdeToolsTests.Text(server, tool, arguments);
            Task<(string Text, bool IsError)> Call(string tool, object arguments) => ClaudeIdeToolsTests.Call(server, tool, arguments);

            // Declarations by name (Roslyn waited for as it loads the project), a member of a type's, of a kind.
            Assert.Equal("Shapes/Circle.cs:3: class Circle", await Text("code_symbols", new { query = "Circle" }));
            Assert.Equal("Shapes/Circle.cs:6: method Circle.Area\nShapes/IShape.cs:6: method IShape.Area\nShapes/Square.cs:5: method Square.Area\nShapes/Square.cs:7: method Square.Area",
                await Text("code_symbols", new { query = "Area" }));
            Assert.Equal("Shapes/Square.cs:5: method Square.Area\nShapes/Square.cs:7: method Square.Area", await Text("code_symbols", new { query = "Square.Area" }));
            Assert.Equal("Shapes/IShape.cs:4: interface IShape", await Text("code_symbols", new { query = "Shape", kind = "interface" }));
            Assert.Equal("No declaration's name matches Nothing.", await Text("code_symbols", new { query = "Nothing" }));

            // A file's outline, with each declaration's lines.
            Assert.Equal("Shapes/Square.cs (8 lines):\n1-8 namespace Geometry\n  3-8 class Square\n    5 method Area() : double\n    7 method Area(int) : double",
                await Text("code_outline", new { path = "Shapes/Square.cs" }));

            // A declaration's source by its name: its doc comment too, every overload, in one file when asked.
            Assert.Equal("Shapes/Circle.cs:5-6, method Circle.Area() : double:\n5\t    /// <summary>πr².</summary>\n6\t    public double Area() => Math.PI * radius * radius;",
                await Text("code_read_symbol", new { symbol = "Circle.Area" }));
            var overloads = await Text("code_read_symbol", new { symbol = "Square.Area" });
            Assert.Contains("Shapes/Square.cs:5, method Square.Area() : double:\n5\t    public double Area() => side * side;", overloads);
            Assert.Contains("Shapes/Square.cs:7, method Square.Area(int) : double:\n7\t    public double Area(int scale) => Area() * scale;", overloads);
            Assert.StartsWith("Shapes/IShape.cs:3-7, interface IShape:\n3\t/// <summary>A shape with an area.</summary>\n4\tpublic interface IShape", await Text("code_read_symbol", new { symbol = "IShape" }));
            Assert.StartsWith("Shapes/Circle.cs:5-6, method Circle.Area()", await Text("code_read_symbol", new { symbol = "Area", path = "Shapes/Circle.cs" }));
            var (missing, missingFailed) = await Call("code_read_symbol", new { symbol = "Nope" });
            Assert.True(missingFailed);
            Assert.Equal("No declaration is named Nope; code_symbols finds names that match.", missing);

            // What a name on a line refers to: the overload called there; a library's, outside the solution.
            Assert.Equal("Program.cs:8, method Report.Total(IEnumerable<IShape>) : double:\n8\t    public static double Total(IEnumerable<IShape> shapes) => shapes.Sum(s => s.Area());",
                await Text("code_definition", new { symbol = "Total", path = "Program.cs", line = 4 }));
            Assert.StartsWith("Shapes/Square.cs:5, method Square.Area() : double:", await Text("code_definition", new { symbol = "Area", path = "Shapes/Square.cs", line = 7, column = 38 }));
            Assert.Contains("(outside the solution)", await Text("code_definition", new { symbol = "WriteLine", path = "Program.cs", line = 4 }));
            Assert.StartsWith("Total is not on line 3 of Program.cs", (await Call("code_definition", new { symbol = "Total", path = "Program.cs", line = 3 })).Text);

            // Every use, by file, with the member each is in; a name declared twice asks which.
            var references = await Text("code_references", new { symbol = "IShape.Area" });
            Assert.StartsWith("5 reference(s) of IShape.Area() : double in 4 file(s):\nProgram.cs\n  8 (in Report.Total(IEnumerable<IShape>)): public static double Total", references);
            Assert.Contains("Shapes/IShape.cs\n  6 (declaration): double Area();", references);
            Assert.Contains("  7 (in Square.Area(int)): public double Area(int scale) => Area() * scale;", references);
            var (ambiguous, ambiguousFailed) = await Call("code_references", new { symbol = "Square.Area" });
            Assert.True(ambiguousFailed);
            Assert.Equal("Square.Area is declared 2 times; give the path and line of the one you mean:\nShapes/Square.cs:5: method Square.Area() : double\nShapes/Square.cs:7: method Square.Area(int) : double", ambiguous);
            Assert.StartsWith("4 reference(s) of Area in 3 file(s):", await Text("code_references", new { symbol = "Area", path = "Shapes/Square.cs", line = 5 }));

            // What implements an interface, or its member.
            Assert.Equal("2 implementation(s) of IShape in 2 file(s):\nShapes/Circle.cs\n  3 (in Circle): public sealed class Circle(double radius) : IShape\nShapes/Square.cs\n  3 (in Square): public sealed class Square(double side) : IShape",
                await Text("code_implementations", new { symbol = "IShape" }));
            Assert.StartsWith("2 implementation(s) of Area in 2 file(s):\nShapes/Circle.cs\n  6 (in Circle.Area()):", await Text("code_implementations", new { symbol = "Area", path = "Program.cs", line = 8 }));

            // Shown in Karvon: the references in Find Results, a declaration read in the editor, its lines selected.
            var tools = ClaudeOf(vm).Claude.Tools!;
            Assert.False((await ClaudeIdeToolsTests.Call(tools, "code_references", new { symbol = "IShape.Area" })).IsError);
            await WaitUntil(() => vm.FindResults.Count > 0, TimeSpan.FromSeconds(10));
            Assert.Equal(5, vm.FindResults.Count);
            Assert.Equal("Claude: references to IShape.Area (5)", vm.FindResultsTitle);
            Assert.False((await ClaudeIdeToolsTests.Call(tools, "code_read_symbol", new { symbol = "Circle.Area" })).IsError);
            await WaitUntil(() => vm.ActiveEditor?.Host.Editor.SelectionLength > 0, TimeSpan.FromSeconds(10));
            Assert.EndsWith("Circle.cs", vm.ActiveEditor?.FilePath);
            Assert.StartsWith("    /// <summary>", vm.ActiveEditor!.Host.Editor.SelectedText);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A language of .hint files whose server outlines only the files open in it, as TypeScript's does.</summary>
    sealed class FakeSymbolsModule : KarvonModule
    {
        public override string Id => "fake-symbols";

        public override string Name => "Fake symbols";

        public override string Description => "A language whose server outlines open files, for the tests.";

        public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
            [new("fake-symbols", "Fake symbols server", path => path.EndsWith(".hint", StringComparison.Ordinal), start => FakeLanguageClient.StartAsync(start, null, "--symbols"))];
    }

    [AvaloniaFact]
    public async Task A_server_that_answers_for_open_files_only_has_the_file_opened_for_the_call()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-code-open-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.hint"), "def alpha\n  use one\ndef beta\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "def gamma\n");
            var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeSymbolsModule()]);
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);
            var server = ClaudeOf(vm).ToolServer();

            // No editor has the file: its server is started for the call, and the file opened in it for as long as it takes.
            Assert.Empty(vm.Editors);
            Assert.Equal("app.hint (4 lines):\n1 function alpha\n3 function beta", await ClaudeIdeToolsTests.Text(server, "code_outline", new { path = "app.hint" }));
            Assert.Equal("app.hint:3, function beta:\n3\tdef beta", await ClaudeIdeToolsTests.Text(server, "code_read_symbol", new { symbol = "beta", path = "app.hint" }));
            Assert.Empty(vm.Editors);
            Assert.Contains("No language server serves notes.txt", (await ClaudeIdeToolsTests.Call(server, "code_outline", new { path = "notes.txt" })).Text);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
