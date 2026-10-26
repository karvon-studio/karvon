using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.TypeScript;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A language whose folder's packages are not installed: offered as its server starts, and by Ctrl+.</summary>
public class PackageRestoreTests
{
    /// <summary>A language of .hint files whose packages are a deps.txt's, installed into a deps folder (by dotnet --version).</summary>
    sealed class FakeRestoreModule : KarvonModule
    {
        public override string Id => "fake-restore";

        public override string Name => "Fake restore";

        public override string Description => "A language with packages to install, for the tests.";

        public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
            [new("fake-restore", "Fake restore server", path => path.EndsWith(".hint", StringComparison.Ordinal), start => FakeLanguageClient.StartAsync(start, null, "--hints"))];

        public override PackageRestore? PackageRestoreIn(string folder) => File.Exists(Path.Combine(folder, "deps.txt"))
            ? new("Install the fake packages", !Directory.Exists(Path.Combine(folder, "deps")), [new("fake: install", WorkspaceTaskKind.Other, "dotnet", ["--version"], folder)])
            : null;

        // The fake server's warning stands for an import it cannot resolve.
        public override bool IsUnresolvedImport(LspDiagnostic diagnostic) => diagnostic.Message == "TODO left";
    }

    sealed class Dialogs : IDialogService
    {
        public List<string> Confirmations { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult<bool?>(true);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [AvaloniaFact]
    public async Task Its_server_starting_offers_the_install_and_Ctrl_dot_has_it_first()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-restore-").FullName;
        File.WriteAllText(Path.Combine(dir, "deps.txt"), "one\n");
        var file = Path.Combine(dir, "app.hint");
        File.WriteAllText(file, "use one // TODO\n");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeRestoreModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        // After the window, which gives the view model its own dialogs.
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await vm.OpenSolutionAsync(dir);

        // The file opens, its server starts: the install asked about, then run in Output (Package Manager).
        var host = vm.OpenDocument(file)!.Host;
        var output = ((IWorkbench)vm).Output("Package Manager");
        await WaitUntil(() => output.Document.Text.Contains("Install the fake packages: done.", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        Assert.Contains("the Fake restore language server cannot resolve their imports.\n\nInstall the fake packages?", Assert.Single(dialogs.Confirmations));
        Assert.Contains("> dotnet --version", output.Document.Text);

        // Ctrl+. on the problem: the install first, then the server's own actions.
        await WaitUntil(() => host.Diagnostics.Count > 0, TimeSpan.FromSeconds(20));
        host.Editor.CaretOffset = host.Document.Text.IndexOf("TODO", StringComparison.Ordinal) + 1;
        var menu = (await host.ShowQuickActionsAsync())!;
        var items = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Equal(["Install the fake packages", "Fix all TODOs"], items);
        menu.Close();
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => output.Document.Text.Split("Install the fake packages: done.").Length == 3, TimeSpan.FromSeconds(20));
        Assert.Single(dialogs.Confirmations);
        window.Close();
    }

    [Fact]
    public void A_package_json_folder_without_node_modules_is_installed_by_its_package_manager()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-npm-restore-").FullName;
        var typescript = new TypeScriptModule();
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "shop", "scripts": { "dev": "vite" } }""");
        Assert.Null(typescript.PackageRestoreIn(dir));

        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "shop", "dependencies": { "left-pad": "^1.3.0" } }""");
        File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "");
        var restore = typescript.PackageRestoreIn(dir)!;
        Assert.Equal(("Install the packages (pnpm install)", true), (restore.Title, restore.IsMissing));
        Assert.Equal([("pnpm", "install", dir)], restore.Steps!.Select(s => (s.Program, string.Join(' ', s.Arguments), s.WorkingDirectory)));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
        Assert.False(typescript.PackageRestoreIn(dir)!.IsMissing);

        // TypeScript's "Cannot find module" is an unresolved import; another problem is not.
        LspDiagnostic Problem(string code) => new(new(new(0, 0), new(0, 1)), DiagnosticSeverity.Error, code, "", false);
        Assert.True(typescript.IsUnresolvedImport(Problem("2307")));
        Assert.False(typescript.IsUnresolvedImport(Problem("2322")));
    }
}
