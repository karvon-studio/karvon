using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.TypeScript;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The TypeScript and JavaScript module: TypeScript 7's own language server in the editor of their files.</summary>
public class TypeScriptTests
{
    [Fact]
    public void It_serves_typescript_and_javascript()
    {
        Assert.Equal("typescript", TypeScriptServer.LanguageIdOf("/w/app.ts"));
        Assert.Equal("typescript", TypeScriptServer.LanguageIdOf("/w/types.d.ts"));
        Assert.Equal("typescript", TypeScriptServer.LanguageIdOf("/w/config.mts"));
        Assert.Equal("typescriptreact", TypeScriptServer.LanguageIdOf("/w/App.TSX"));
        Assert.Equal("javascript", TypeScriptServer.LanguageIdOf("/w/server.cjs"));
        Assert.Equal("javascriptreact", TypeScriptServer.LanguageIdOf("/w/App.jsx"));
        Assert.False(TypeScriptServer.Serves("/w/Program.cs"));
        Assert.False(TypeScriptServer.Serves("/w/package.json"));
        Assert.Contains(Karvon.BuiltInModules.Create(), m => m.Id == "typescript" && m.LanguageServers.Single(s => s.Id == "typescript").Serves("/w/app.ts"));
    }

    [Fact]
    public void Html_css_and_json_files_have_their_servers_languages()
    {
        Assert.Equal(("html", "html"), (WebLanguageServer.LanguageOf("/w/index.htm"), WebLanguageServer.LanguageIdOf("/w/index.htm")));
        Assert.Equal(("css", "scss"), (WebLanguageServer.LanguageOf("/w/site.scss"), WebLanguageServer.LanguageIdOf("/w/site.scss")));
        // JSON with comments where VS Code allows them: tsconfig, jsconfig, .vscode's files, .jsonc.
        Assert.Equal(["json", "jsonc", "jsonc", "jsonc"],
            new[] { "/w/package.json", "/w/tsconfig.app.json", "/w/.vscode/launch.json", "/w/notes.jsonc" }.Select(WebLanguageServer.LanguageIdOf));
        Assert.Null(WebLanguageServer.LanguageOf("/w/app.ts"));
    }

    [AvaloniaFact]
    public async Task Html_css_and_json_are_served_by_vs_codes_servers()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1" && Workspace.TaskVariables.Find("node",
            string.Join(Path.PathSeparator, new[] { Build.NodeLocator.DirectoryOffPath(), Environment.GetEnvironmentVariable("PATH") }.OfType<string>()), "/") is not null,
            "Set KARVON_TYPESCRIPT_LIVE=1, with Node.js installed, to install VS Code's HTML, CSS and JSON servers and try them.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var page = Path.Combine(dir, "index.html");
            File.WriteAllText(page, "<!DOCTYPE html>\n<html>\n<head><style>p { color: red; }</style></head>\n<bo\n</html>\n");
            var style = Path.Combine(dir, "site.css");
            File.WriteAllText(style, "body {\n  color: ;\n}\n");
            var data = Path.Combine(dir, "data.json");
            File.WriteAllText(data, "{ \"name\": \"shop\", }\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // HTML: its elements as it is typed.
            var html = vm.OpenDocument(page)!;
            await WaitUntil(() => html.Host.HasLanguageServer, TimeSpan.FromMinutes(3));
            Assert.True(html.Host.HasLanguageServer, vm.ServerOutput.Document.Text);
            var htmlServer = Assert.IsType<WebLanguageServer>(vm.LanguageServerFor(page));
            Assert.Contains((await htmlServer.CompletionAsync(page, new LspPosition(3, 3), null, CancellationToken.None)).Items, i => i.Label == "body");

            // CSS and JSON: their errors in the editor, each from its own server.
            var css = vm.OpenDocument(style)!;
            await WaitUntil(() => css.Host.Diagnostics.Count > 0, TimeSpan.FromSeconds(60));
            Assert.Equal(1, css.Host.Diagnostics[0].Range.Start.Line);
            var json = vm.OpenDocument(data)!;
            await WaitUntil(() => json.Host.Diagnostics.Count > 0, TimeSpan.FromSeconds(60));
            Assert.Contains(json.Host.Diagnostics, d => d.Message.Contains("comma", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, vm.ModuleLanguageServers.Count(s => s is WebLanguageServer));
            Assert.NotSame(vm.LanguageServerFor(style), vm.LanguageServerFor(data));
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Its_server_checks_the_code_as_it_is_typed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1",
            "Set KARVON_TYPESCRIPT_LIVE=1 to install TypeScript 7 from the npm registry and try its language server.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tsconfig.json"), """{ "compilerOptions": { "strict": true, "target": "es2022", "module": "nodenext" } }""");
            File.WriteAllText(Path.Combine(dir, "shapes.ts"), "export function area(width: number, height: number): number {\n    return width * height;\n}\n");
            var app = Path.Combine(dir, "app.ts");
            File.WriteAllText(app, "import { area } from \"./shapes.js\";\n\nconst size: number = \"large\";\nconsole.log(area(2, 3), size);\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // The first .ts file installs TypeScript and starts its server; the type error shows in the editor.
            var doc = vm.OpenDocument(app)!;
            await WaitUntil(() => doc.Host.Diagnostics.Count > 0, TimeSpan.FromMinutes(3));
            var error = Assert.Single(doc.Host.Diagnostics);
            Assert.Equal("2322", error.Code);
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Equal(2, error.Range.Start.Line);
            var server = Assert.IsType<TypeScriptServer>(Assert.Single(vm.ModuleLanguageServers));

            // The fix, typed: the error goes.
            var line = doc.Host.Document.GetLineByNumber(3);
            doc.Host.Document.Replace(line.Offset, line.Length, "const size: number = 42;");
            await WaitUntil(() => doc.Host.Diagnostics.Count == 0, TimeSpan.FromSeconds(30));
            Assert.Empty(doc.Host.Diagnostics);

            // Completion, hover and go to definition across the files.
            var members = await server.CompletionAsync(app, new LspPosition(3, 0), null, CancellationToken.None);
            Assert.Contains(members.Items, i => i.Label == "area");
            Assert.Contains("number", await server.HoverAsync(app, new LspPosition(3, 13), CancellationToken.None));
            var definition = Assert.Single(await server.DefinitionAsync(app, new LspPosition(3, 13), CancellationToken.None));
            Assert.EndsWith("shapes.ts", new Uri(definition.Uri).LocalPath);
            Assert.Equal(0, definition.Range.Start.Line);

            // A JavaScript file is served by the same server.
            var script = Path.Combine(dir, "tool.js");
            File.WriteAllText(script, "export const twice = (n) => n * 2;\n");
            var js = vm.OpenDocument(script)!;
            await WaitUntil(() => js.Host.HasLanguageServer, TimeSpan.FromSeconds(10));
            Assert.Same(server, Assert.Single(vm.ModuleLanguageServers));

            doc.Host.Save();
            Assert.True(await vm.CloseSolutionCoreAsync());
            Assert.Empty(vm.ModuleLanguageServers);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
