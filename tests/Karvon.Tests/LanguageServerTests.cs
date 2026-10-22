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

sealed class FakeLanguageClient : LspClient
{
    FakeLanguageClient(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public override string LanguageIdFor(string path) => path.EndsWith(".fake", StringComparison.Ordinal) ? "fake" : "plaintext";

    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var (process, rpc) = Launch(new ProcessStartInfo("python3", ["-I", Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeLsp", "lsp_server.py")]));
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
}
