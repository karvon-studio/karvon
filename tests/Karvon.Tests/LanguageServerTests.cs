using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A module's language server, here the tests' fake one for .fake files (TestAssets/FakeLsp/lsp_server.py).</summary>
sealed class FakeLanguageModule : KarvonModule
{
    public override string Id => "fake-language";

    public override string Name => "Fake language";

    public override string Description => "A language server for the tests.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("fake", "Fake language server", path => path.EndsWith(".fake", StringComparison.Ordinal), FakeLanguageClient.StartAsync)];
}

/// <summary>A linter of the .fake files beside their server: the same fake server, warning of each FIXME.</summary>
sealed class FakeLinterModule(bool allowed) : KarvonModule
{
    public override string Id => "fake-linter";

    public override string Name => "Fake linter";

    public override string Description => "A linter for the tests.";

    /// <summary>How many times it was asked whether it may start.</summary>
    public int Asked { get; private set; }

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
    [
        new("fake-lint", "Fake linter", path => path.EndsWith(".fake", StringComparison.Ordinal), start => FakeLanguageClient.StartAsync(start, "FIXME"))
        {
            Lints = true,
            MayStartAsync = (_, _) =>
            {
                Asked++;
                return Task.FromResult(allowed);
            },
        },
    ];
}

sealed class FakeLanguageClient : LspClient
{
    FakeLanguageClient(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public override string LanguageIdFor(string path) =>
        path.EndsWith(".fake", StringComparison.Ordinal) || path.EndsWith(".hint", StringComparison.Ordinal) ? "fake" : "plaintext";

    public static Task<LspClient> StartAsync(LanguageServerStart start) => StartAsync(start, null);

    /// <param name="word">A linter's: the word it warns of (else TODO).</param>
    /// <param name="flags">The server's own (--hints).</param>
    public static async Task<LspClient> StartAsync(LanguageServerStart start, string? word, params string[] flags)
    {
        var (process, rpc) = Launch(new ProcessStartInfo(TestTools.Python, ["-I", Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeLsp", "lsp_server.py"),
            .. word is null ? Array.Empty<string>() : [word], .. flags]));
        var client = new FakeLanguageClient(process, rpc, start.Workspace);
        await client.InitializeAsync(start.Workspace, null, start.Cancellation);
        return client;
    }
}

/// <summary>A module's language server serves its files in the editor: it starts when the first opens, and stops with the workspace.</summary>
public class LanguageServerTests
{
    [AvaloniaFact]
    public async Task A_modules_language_server_serves_its_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "plan.fake");
        File.WriteAllText(file, "first line\nTODO: second\n");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "TODO here is only text\n");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeLanguageModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // Nothing starts before one of its files opens; another file does not start it.
        Assert.Empty(vm.ModuleLanguageServers);
        vm.OpenDocument(Path.Combine(dir, "notes.txt"));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Assert.Empty(vm.ModuleLanguageServers);

        // Its file: the server starts and pushes the file's diagnostics, which the editor shows.
        var doc = vm.OpenDocument(file)!;
        await WaitUntil(() => doc.Host.Diagnostics.Count > 0, TimeSpan.FromSeconds(10));
        var warning = Assert.Single(doc.Host.Diagnostics);
        Assert.Equal("TODO left", warning.Message);
        Assert.Equal(1, warning.Range.Start.Line);
        var server = Assert.Single(vm.ModuleLanguageServers);
        Assert.True(server.IsReady);
        Assert.True(server.Handles(file));
        Assert.False(server.Handles(Path.Combine(dir, "notes.txt")));

        // An edit reaches it: the diagnostics follow.
        doc.Host.Document.Insert(0, "TODO ");
        await WaitUntil(() => doc.Host.Diagnostics.Count == 2, TimeSpan.FromSeconds(10));
        Assert.Equal(2, doc.Host.Diagnostics.Count);

        // Its answers: completion and hover.
        var completion = await server.CompletionAsync(file, new LspPosition(0, 0), null, CancellationToken.None);
        Assert.Equal(["fakeAlpha", "fakeBeta"], completion.Items.Select(i => i.Label));
        Assert.Equal("Fake hover", await server.HoverAsync(file, new LspPosition(0, 0), CancellationToken.None));

        // The workspace closes (the edit saved, so nothing asks): the server stops.
        doc.Host.Save();
        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.Empty(vm.ModuleLanguageServers);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_linter_adds_its_diagnostics_beside_the_files_server()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "plan.fake");
        File.WriteAllText(file, "TODO: first\nFIXME: second\n");
        var linter = new FakeLinterModule(allowed: true);
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), linter, new FakeLanguageModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // The file's server (the first that is not a linter, though the linter's module comes first) and the linter, both
        // on the file: the editor shows the diagnostics of both.
        var doc = vm.OpenDocument(file)!;
        static string[] Messages(DocumentViewModel d) => [.. d.Host.Diagnostics.Select(x => x.Message).Order()];
        await WaitUntil(() => Messages(doc) is ["FIXME left", "TODO left"], TimeSpan.FromSeconds(10));
        Assert.Equal(["FIXME left", "TODO left"], Messages(doc));
        Assert.Equal(1, linter.Asked);
        Assert.Equal(2, vm.ModuleLanguageServers.Count());
        var server = vm.LanguageServerFor(file)!;
        var lint = Assert.Single(vm.LintersFor(file));
        Assert.NotSame(server, lint);
        Assert.Equal([server, lint], vm.LanguageServersForClaude(file));
        Assert.Same(lint, Assert.Single(doc.Host.Linters));
        Assert.True(doc.Host.HasLanguageServer);

        // An edit reaches both: each one's diagnostics follow it.
        doc.Host.Document.Insert(0, "FIXME TODO ");
        await WaitUntil(() => doc.Host.Diagnostics.Count == 4, TimeSpan.FromSeconds(10));
        Assert.Equal(["FIXME left", "FIXME left", "TODO left", "TODO left"], Messages(doc));

        // Claude's code_diagnostics has both too.
        var (text, _) = await ClaudeIdeToolsTests.Call(ClaudeOf(vm).ToolServer(), "code_diagnostics", new { path = "plan.fake" });
        Assert.Equal(2, text.Split("FIXME left").Length - 1);
        Assert.Equal(2, text.Split("TODO left").Length - 1);

        // Restart Language Server: both start again, and the linter is on the file again (asked again, for its module to say).
        await vm.RestartLanguageServerCommand.ExecuteAsync(null);
        await WaitUntil(() => doc.Host.Linters.Count == 1 && doc.Host.Linters[0] != lint && Messages(doc).Length == 4, TimeSpan.FromSeconds(10));
        Assert.Equal(4, doc.Host.Diagnostics.Count);
        Assert.Equal(2, linter.Asked);

        // The linter stops: its diagnostics go, the server's stay.
        doc.Host.DetachLinter(doc.Host.Linters[0]);
        await WaitUntil(() => doc.Host.Diagnostics.Count == 2, TimeSpan.FromSeconds(10));
        Assert.Equal(["TODO left", "TODO left"], Messages(doc));
        doc.Host.Save();
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_linter_that_may_not_start_stays_off_while_the_workspace_is_open()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.fake"), "TODO FIXME\n");
        File.WriteAllText(Path.Combine(dir, "b.fake"), "FIXME\n");
        var linter = new FakeLinterModule(allowed: false);
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeLanguageModule(), linter]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        var a = vm.OpenDocument(Path.Combine(dir, "a.fake"))!;
        await WaitUntil(() => a.Host.Diagnostics.Count > 0, TimeSpan.FromSeconds(10));
        var b = vm.OpenDocument(Path.Combine(dir, "b.fake"))!;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
        Assert.Equal("TODO left", Assert.Single(a.Host.Diagnostics).Message);
        Assert.Empty(b.Host.Diagnostics);
        Assert.Empty(a.Host.Linters);
        Assert.Single(vm.ModuleLanguageServers);
        // Asked once for the workspace, not for each file.
        Assert.Equal(1, linter.Asked);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }
}
