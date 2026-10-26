using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Python;
using Karvon.ViewModels;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A folder whose packages are nowhere: its virtual environment made, as Pyright starts or from Ctrl+.</summary>
public class PythonEnvironmentTests
{
    static string Folder(params (string Name, string Text)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("karvon-pyenv-").FullName;
        foreach (var (name, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
        return dir;
    }

    [Fact]
    public void What_a_folder_is_offered_and_the_steps_that_make_it()
    {
        // A python3 of its own on the PATH (the steps name it; nothing runs here).
        var bin = Folder(("python3", "#!/bin/sh\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(bin, "python3"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var python = OperatingSystem.IsWindows() ? "python.exe" : "python3";
        if (OperatingSystem.IsWindows())
            File.Move(Path.Combine(bin, "python3"), Path.Combine(bin, python));

        // requirements.txt, no environment: one named as its .gitignore leaves it out, requirements.txt installed into it.
        var plain = Folder(("requirements.txt", "requests==2.32.3\n"), (".gitignore", "__pycache__/\n/venv/\n"));
        Assert.True(PythonEnvironmentSetup.IsMissing(plain));
        Assert.Equal("Create a virtual environment (venv) and install requirements.txt", PythonEnvironmentSetup.Offer(plain));
        var steps = PythonEnvironmentSetup.Steps(plain, bin)!;
        Assert.Equal([(Path.Combine(bin, python), "-m venv venv"), (PythonEnvironment.InterpreterIn(Path.Combine(plain, "venv")), "-m pip install -r requirements.txt")],
            steps.Select(s => (s.Program, string.Join(' ', s.Arguments))));
        Assert.All(steps, s => Assert.Equal(plain, s.WorkingDirectory));
        // No .gitignore naming one: .venv, as VS Code makes it.
        Assert.Equal(".venv", PythonEnvironmentSetup.VenvName(Folder(("requirements.txt", ""))));
        // No Python at all: no steps.
        Assert.Null(PythonEnvironmentSetup.Steps(plain, Folder()));

        // An environment already: requirements.txt installed into it.
        var venv = PythonEnvironment.InterpreterIn(Path.Combine(plain, "venv"));
        Directory.CreateDirectory(Path.GetDirectoryName(venv)!);
        File.WriteAllText(venv, "");
        Assert.False(PythonEnvironmentSetup.IsMissing(plain));
        Assert.Equal("Install requirements.txt into the virtual environment", PythonEnvironmentSetup.Offer(plain));
        Assert.Equal([(venv, "-m pip install -r requirements.txt")], PythonEnvironmentSetup.Steps(plain, bin)!.Select(s => (s.Program, string.Join(' ', s.Arguments))));

        // A uv project: uv sync makes its .venv; nothing once it has one. A folder of neither: nothing.
        var uv = Folder(("pyproject.toml", "[project]\nname = \"shop\"\n"), ("uv.lock", ""));
        Assert.Equal("Sync the uv project (uv sync)", PythonEnvironmentSetup.Offer(uv));
        Assert.Equal([("uv", "sync")], PythonEnvironmentSetup.Steps(uv, bin)!.Select(s => (s.Program, string.Join(' ', s.Arguments))));
        var uvVenv = PythonEnvironment.InterpreterIn(Path.Combine(uv, ".venv"));
        Directory.CreateDirectory(Path.GetDirectoryName(uvVenv)!);
        File.WriteAllText(uvVenv, "");
        Assert.Null(PythonEnvironmentSetup.Offer(uv));
        Assert.Null(PythonEnvironmentSetup.Offer(Folder(("main.py", "print(1)\n"))));
        Assert.False(PythonEnvironmentSetup.IsMissing(Folder(("main.py", "print(1)\n"))));
    }

    sealed class Dialogs : IDialogService
    {
        public List<string> Confirmations { get; } = [];

        public bool? Answer { get; set; } = true;

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [AvaloniaFact]
    public async Task A_folder_without_its_environment_gets_one_and_Ctrl_dot_installs_into_it()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Assert.SkipUnless(PythonEnvironment.Interpreter(Folder(), path) is not null, "Python 3 is not installed.");
        // Nothing to download: requirements.txt has only a comment.
        var dir = Folder(("requirements.txt", "# none yet\n"), (".gitignore", "venv\n"), ("main.py", "import missing_package\n"));
        var vm = NewViewModel();
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await vm.OpenSolutionAsync(dir);
        var python = vm.Module<PythonModule>()!;

        // Its language server starting asks once a session; yes makes the environment, in Output (Package Manager).
        await vm.OfferPackageRestoreAsync(python, dir);
        Assert.Contains("Create a virtual environment (venv) and install requirements.txt?", Assert.Single(dialogs.Confirmations));
        var output = ((IWorkbench)vm).Output("Package Manager");
        Assert.True(File.Exists(PythonEnvironment.InterpreterIn(Path.Combine(dir, "venv"))), output.Document.Text);
        Assert.Contains("-m pip install -r requirements.txt", output.Document.Text);
        Assert.Contains("install requirements.txt: done.", output.Document.Text);
        await vm.OfferPackageRestoreAsync(python, dir);
        Assert.Single(dialogs.Confirmations);

        // Ctrl+. on an import Pyright cannot resolve: requirements.txt installed into the environment; nothing for another problem.
        LspDiagnostic Problem(string code) => new(new(new(0, 7), new(0, 22)), DiagnosticSeverity.Error, code, "Import \"missing_package\" could not be resolved", false);
        var action = Assert.Single(vm.PackageQuickActions(Path.Combine(dir, "main.py"), [Problem("reportMissingImports")]));
        Assert.Equal("Install requirements.txt into the virtual environment", action.Title);
        Assert.Empty(vm.PackageQuickActions(Path.Combine(dir, "main.py"), [Problem("reportAttributeAccessIssue")]));
        await action.RunAsync();
        Assert.Contains("Install requirements.txt into the virtual environment: done.", output.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());

        // A no is kept with the folder's state.
        var other = Folder(("requirements.txt", "# none\n"), ("main.py", "print(1)\n"));
        dialogs.Answer = false;
        await vm.OpenSolutionAsync(other);
        await vm.OfferPackageRestoreAsync(python, other);
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.Equal("python", ((IWorkbench)vm).WorkspaceState["packages.declined"]);
        Assert.False(Directory.Exists(Path.Combine(other, ".venv")));
        Assert.True(await vm.CloseSolutionCoreAsync());
    }
}
