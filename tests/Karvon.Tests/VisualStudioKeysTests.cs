using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Karvon.Editor;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Visual Studio's keys, pressed: the window's (tool windows, chords, Go To Line...) and the text editor's.</summary>
public class VisualStudioKeysTests
{
    sealed class Dialogs : IDialogService
    {
        public Queue<string?> Answers { get; } = new();

        public List<string> Prompts { get; } = [];

        public List<string> Files { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial)
        {
            Prompts.Add($"{title}: {label} [{initial}]");
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
        }

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message) => Task.FromResult<bool?>(true);

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([.. Files]);
    }

    /// <summary>A folder with the files in it, opened, the first in an editor with the focus.</summary>
    static async Task<(MainViewModel Vm, MainWindow Window, Dialogs Dialogs, EditorHost Host)> OpenAsync(params (string Name, string Text)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-vs-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(dir, name), text);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        var host = vm.OpenDocument(Path.Combine(dir, files[0].Name))!.Host;
        await WaitUntil(() => host.Editor.TextArea.IsEffectivelyVisible && TopLevel.GetTopLevel(host.Editor) is not null, TimeSpan.FromSeconds(5));
        host.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return (vm, window, dialogs, host);
    }

    static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical)
    {
        window.KeyPress(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    const RawInputModifiers Ctrl = RawInputModifiers.Control;
    const RawInputModifiers Shift = RawInputModifiers.Shift;
    const RawInputModifiers Alt = RawInputModifiers.Alt;

    [AvaloniaFact]
    public async Task The_editor_duplicates_moves_cuts_deletes_and_opens_lines()
    {
        var (_, window, _, host) = await OpenAsync(("notes.txt", "one\n  two\nthree\n"));
        void At(string text, int caret)
        {
            host.Document.Text = text;
            host.Editor.TextArea.ClearSelection();
            host.Editor.CaretOffset = caret;
        }

        // Ctrl+D: the line again below, the caret on the copy; a selection again after it, the copy selected.
        At("one\n  two\nthree\n", 1);
        Press(window, Key.D, Ctrl, PhysicalKey.D);
        Assert.Equal("one\none\n  two\nthree\n", host.Document.Text);
        Assert.Equal((2, 2), host.CaretPosition);
        host.Editor.Select(4, 3);
        Press(window, Key.D, Ctrl, PhysicalKey.D);
        Assert.Equal("one\noneone\n  two\nthree\n", host.Document.Text);
        Assert.Equal((7, 3), (host.Editor.SelectionStart, host.Editor.SelectionLength));

        // Alt+Down, Alt+Up: the lines moved, the caret with them, a selection of two lines too.
        At("one\n  two\nthree\n", 1);
        Press(window, Key.Down, Alt, PhysicalKey.ArrowDown);
        Assert.Equal("  two\none\nthree\n", host.Document.Text);
        Assert.Equal((2, 2), host.CaretPosition);
        Press(window, Key.Up, Alt, PhysicalKey.ArrowUp);
        Assert.Equal("one\n  two\nthree\n", host.Document.Text);
        host.Editor.Select(0, 10);
        Press(window, Key.Down, Alt, PhysicalKey.ArrowDown);
        Assert.Equal("three\none\n  two\n", host.Document.Text);
        Assert.Equal("one\n  two\n", host.Editor.SelectedText);

        // Ctrl+Shift+L deletes the line; Ctrl+L cuts it.
        At("one\n  two\nthree", 6);
        Press(window, Key.L, Ctrl | Shift, PhysicalKey.L);
        Assert.Equal("one\nthree", host.Document.Text);
        host.Editor.CaretOffset = 6;
        Press(window, Key.L, Ctrl | Shift, PhysicalKey.L);
        Assert.Equal("one", host.Document.Text);
        At("one\n  two\nthree\n", 6);
        Press(window, Key.L, Ctrl, PhysicalKey.L);
        Assert.Equal("one\nthree\n", host.Document.Text);

        // Ctrl+Enter: a line above, indented as the caret's; Ctrl+Shift+Enter: below.
        At("one\n  two\nthree\n", 8);
        Press(window, Key.Enter, Ctrl, PhysicalKey.Enter);
        Assert.Equal("one\n  \n  two\nthree\n", host.Document.Text);
        Assert.Equal((2, 3), host.CaretPosition);
        At("one\n  two\nthree\n", 5);
        Press(window, Key.Enter, Ctrl | Shift, PhysicalKey.Enter);
        Assert.Equal("one\n  two\n  \nthree\n", host.Document.Text);
        Assert.Equal((3, 3), host.CaretPosition);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_editor_changes_case_goes_to_braces_selects_words_and_finds_the_selection()
    {
        var (_, window, _, host) = await OpenAsync(("notes.txt", ""));
        host.Document.Text = "call(a[1], b) + Foo; foo = FOO";

        // Ctrl+Shift+U, Ctrl+U: the selection in upper, then lower case, still selected.
        host.Editor.Select(0, 4);
        Press(window, Key.U, Ctrl | Shift, PhysicalKey.U);
        Assert.StartsWith("CALL(", host.Document.Text);
        Assert.Equal("CALL", host.Editor.SelectedText);
        Press(window, Key.U, Ctrl, PhysicalKey.U);
        Assert.StartsWith("call(", host.Document.Text);

        // Ctrl+]: from before "(" to after its ")", and back; Ctrl+Shift+]: what is between selected.
        host.Editor.TextArea.ClearSelection();
        host.Editor.CaretOffset = 4;
        Press(window, Key.OemCloseBrackets, Ctrl, PhysicalKey.BracketRight);
        Assert.Equal(13, host.Editor.CaretOffset);
        Press(window, Key.OemCloseBrackets, Ctrl, PhysicalKey.BracketRight);
        Assert.Equal(4, host.Editor.CaretOffset);
        Press(window, Key.OemCloseBrackets, Ctrl | Shift, PhysicalKey.BracketRight);
        Assert.Equal("(a[1], b)", host.Editor.SelectedText);

        // Ctrl+W: the word at the caret; Ctrl+F3: its next match, whatever its case, from the top after the end.
        host.Editor.TextArea.ClearSelection();
        host.Editor.CaretOffset = 17;
        Press(window, Key.W, Ctrl, PhysicalKey.W);
        Assert.Equal((16, "Foo"), (host.Editor.SelectionStart, host.Editor.SelectedText));
        Press(window, Key.F3, Ctrl, PhysicalKey.F3);
        Assert.Equal(21, host.Editor.SelectionStart);
        Press(window, Key.F3, Ctrl, PhysicalKey.F3);
        Assert.Equal(27, host.Editor.SelectionStart);
        Press(window, Key.F3, Ctrl, PhysicalKey.F3);
        Assert.Equal(16, host.Editor.SelectionStart);
        Press(window, Key.F3, Ctrl | Shift, PhysicalKey.F3);
        Assert.Equal(27, host.Editor.SelectionStart);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Lines_are_commented_in_the_files_language()
    {
        var (vm, window, _, python) = await OpenAsync(("app.py", "x = 1\n    y = 2\n"), ("site.xml", "<a />\n"));

        // Ctrl+K, Ctrl+/: out, then back in (a Python file's #).
        python.Editor.Select(0, 10);
        Press(window, Key.K, Ctrl, PhysicalKey.K);
        Press(window, Key.OemQuestion, Ctrl, PhysicalKey.Slash);
        Assert.Equal("#x = 1\n    #y = 2\n", python.Document.Text);
        Press(window, Key.K, Ctrl, PhysicalKey.K);
        Press(window, Key.OemQuestion, Ctrl, PhysicalKey.Slash);
        Assert.Equal("x = 1\n    y = 2\n", python.Document.Text);

        // Ctrl+K, Ctrl+C in XML: <!-- -->.
        var xml = vm.OpenDocument(Path.Combine(Path.GetDirectoryName(python.FilePath)!, "site.xml"))!.Host;
        await WaitUntil(() => xml.Editor.TextArea.IsEffectivelyVisible, TimeSpan.FromSeconds(5));
        xml.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.K, Ctrl, PhysicalKey.K);
        Press(window, Key.C, Ctrl, PhysicalKey.C);
        Assert.Equal("<!--<a />-->\n", xml.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_windows_keys_show_the_tool_windows_and_go_where_asked()
    {
        var (vm, window, dialogs, host) = await OpenAsync(("notes.txt", "one\ntwo\nthree\n"), ("other.txt", "other\n"));

        // Ctrl+Alt+L, Ctrl+Alt+O, Ctrl+Alt+C; the chords Ctrl+\, E / Ctrl+Alt+V, L / Ctrl+Alt+W, 1 / Ctrl+E, T.
        Press(window, Key.L, Ctrl | Alt, PhysicalKey.L);
        Assert.Equal(ToolWindows.SolutionExplorer, vm.LastRequestedToolWindow);
        Press(window, Key.O, Ctrl | Alt, PhysicalKey.O);
        Assert.Equal(ToolWindows.Output, vm.LastRequestedToolWindow);
        Press(window, Key.C, Ctrl | Alt, PhysicalKey.C);
        Assert.Equal(ToolWindows.CallStack, vm.LastRequestedToolWindow);
        Press(window, Key.OemPipe, Ctrl, PhysicalKey.Backslash);
        Assert.Equal("(Ctrl+\\) was pressed. Waiting for second key of chord...", vm.StatusText);
        Press(window, Key.E, RawInputModifiers.None, PhysicalKey.E);
        Assert.Equal(ToolWindows.ErrorList, vm.LastRequestedToolWindow);
        Press(window, Key.V, Ctrl | Alt, PhysicalKey.V);
        Press(window, Key.L, RawInputModifiers.None, PhysicalKey.L);
        Assert.Equal(ToolWindows.Locals, vm.LastRequestedToolWindow);
        Press(window, Key.W, Ctrl | Alt, PhysicalKey.W);
        Press(window, Key.D1, RawInputModifiers.None, PhysicalKey.Digit1);
        Assert.Equal(ToolWindows.Watch, vm.LastRequestedToolWindow);
        Press(window, Key.E, Ctrl, PhysicalKey.E);
        Press(window, Key.T, RawInputModifiers.None, PhysicalKey.T);
        Assert.Equal(ToolWindows.TestExplorer, vm.LastRequestedToolWindow);
        Press(window, Key.R, Ctrl, PhysicalKey.R);
        Press(window, Key.Q, RawInputModifiers.None, PhysicalKey.Q);
        Assert.Equal("The key combination (Ctrl+R, Q) is not a command.", vm.StatusText);
        Assert.Equal("one\ntwo\nthree\n", host.Document.Text);

        // Ctrl+G: the line asked for; Ctrl+Shift+F: Find in Files asks what.
        dialogs.Answers.Enqueue("3");
        Press(window, Key.G, Ctrl, PhysicalKey.G);
        Assert.Equal("Go To Line: Line number (1 - 4): [1]", dialogs.Prompts[^1]);
        Assert.Equal((3, 1), host.CaretPosition);
        Press(window, Key.F, Ctrl | Shift, PhysicalKey.F);
        Assert.StartsWith("Find in Files: Find what:", dialogs.Prompts[^1]);

        // Ctrl+O opens a file; Ctrl+F6 / Ctrl+Shift+F6 go from document to document.
        dialogs.Files.Add(Path.Combine(Path.GetDirectoryName(host.FilePath)!, "other.txt"));
        Press(window, Key.O, Ctrl, PhysicalKey.O);
        await WaitUntil(() => vm.ActiveDocument?.Title == "other.txt", TimeSpan.FromSeconds(5));
        Press(window, Key.F6, Ctrl, PhysicalKey.F6);
        Assert.Equal("notes.txt", vm.ActiveDocument?.Title);
        Press(window, Key.F6, Ctrl | Shift, PhysicalKey.F6);
        Assert.Equal("other.txt", vm.ActiveDocument?.Title);

        // Ctrl+Shift+F9 deletes every breakpoint; Shift+Alt+Enter: full screen, and back.
        vm.Breakpoints.Replace(host.FilePath, [1, 2]);
        Press(window, Key.F9, Ctrl | Shift, PhysicalKey.F9);
        Assert.Empty(vm.Breakpoints.All.Values.SelectMany(lines => lines));
        Press(window, Key.Enter, Shift | Alt, PhysicalKey.Enter);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Press(window, Key.Enter, Shift | Alt, PhysicalKey.Enter);
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Close();
    }
}
