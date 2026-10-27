using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Karvon.Editor;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The tests' inline completions: what each request was, and the suggestion it answers with.</summary>
sealed class FakeCompletionsModule(Func<InlineCompletionRequest, string?> answer) : KarvonModule
{
    public override string Id => "fake-completions";

    public override string Name => "Fake completions";

    public override string Description => "Inline completions for the tests.";

    public List<InlineCompletionRequest> Requests { get; } = [];

    public override Func<InlineCompletionRequest, CancellationToken, Task<string?>>? InlineCompletions => (request, _) =>
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    };
}

/// <summary>Inline completions in the editor: a suggestion in gray after the caret, taken with Tab, dismissed with Escape.</summary>
public class InlineCompletionTests
{
    static async Task<(MainViewModel Vm, Window Window, EditorHost Host)> OpenAsync(string text, FakeCompletionsModule module)
    {
        EditorHost.InlineCompletionDelay = TimeSpan.FromMilliseconds(50);
        var dir = Path.Combine(Path.GetTempPath(), "karvon-inline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, text);
        // Before Karvon's own, whose suggestions the editor would take first.
        var vm = NewViewModel(modules: [module, .. Karvon.BuiltInModules.Create()]);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var host = vm.OpenDocument(file)!.Host;
        await WaitUntil(() => host.Editor.TextArea.IsEffectivelyVisible && TopLevel.GetTopLevel(host.Editor) is not null, TimeSpan.FromSeconds(5));
        host.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return (vm, window, host);
    }

    static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(key, modifiers, physical, null);

    [AvaloniaFact]
    public async Task A_suggestion_shows_after_typing_its_lines_push_the_code_down_and_Tab_takes_it()
    {
        var module = new FakeCompletionsModule(_ => "ond\nthird");
        var (_, window, host) = await OpenAsync("a\n\nz\n", module);
        host.Editor.CaretOffset = 2;
        var view = host.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var lineHeight = view.DefaultLineHeight;

        host.Editor.TextArea.PerformTextInput("sec");
        await WaitUntil(() => host.InlineSuggestion is not null, TimeSpan.FromSeconds(5));
        Assert.Equal("ond\nthird", host.InlineSuggestion);
        var request = Assert.Single(module.Requests);
        Assert.Equal("a\nsec", request.Prefix);
        Assert.Equal("\nz\n", request.Suffix);
        Assert.Equal("a\nsec\nz\n", host.Document.Text);

        // Its second line is a line of the view under the first, the code after it a line further down; the caret stays a line high.
        view.EnsureVisualLines();
        var line = view.GetVisualLine(2)!;
        Assert.Equal(2, line.TextLines.Count);
        Assert.Equal(line.VisualTop + 2 * lineHeight, view.GetVisualLine(3)!.VisualTop, 1);
        var caret = host.Editor.TextArea.Caret.CalculateCaretRectangle();
        Assert.True(caret.Height < lineHeight * 1.5, $"the caret is {caret.Height} high, a line {lineHeight}");
        Assert.Equal(line.VisualTop, caret.Top, 1);

        // What it says, typed: the rest of it stays, nothing asked again.
        host.Editor.TextArea.PerformTextInput("o");
        Assert.Equal("nd\nthird", host.InlineSuggestion);
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(module.Requests);

        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Equal("a\nsecond\nthird\nz\n", host.Document.Text);
        Assert.Null(host.InlineSuggestion);
        Assert.Equal("a\nsecond\nthird".Length, host.Editor.CaretOffset);

        // One undo step takes it out again.
        host.Editor.Undo();
        Assert.Equal("a\nseco\nz\n", host.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_moving_away_and_other_typing_dismiss_it_and_Ctrl_Right_takes_a_word()
    {
        var module = new FakeCompletionsModule(request =>
            request.Prefix.EndsWith("Console.", StringComparison.Ordinal) ? "WriteLine(\"hi\");"
            : request.Prefix.EndsWith('(') ? "\"hi\");"
            : null);
        var (_, window, host) = await OpenAsync("Console\n", module);
        host.Editor.CaretOffset = "Console".Length;

        host.Editor.TextArea.PerformTextInput(".");
        await WaitUntil(() => host.InlineSuggestion is not null, TimeSpan.FromSeconds(5));
        Assert.Equal("WriteLine(\"hi\");", host.InlineSuggestion);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(host.InlineSuggestion);
        Assert.Equal("Console.\n", host.Document.Text);

        // Alt+\ asks at once; Ctrl+Right takes its next word, then the next part.
        Press(window, Key.OemPipe, PhysicalKey.Backslash, RawInputModifiers.Alt);
        await WaitUntil(() => host.InlineSuggestion is not null, TimeSpan.FromSeconds(5));
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Assert.Equal("Console.WriteLine\n", host.Document.Text);
        Assert.Equal("(\"hi\");", host.InlineSuggestion);
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Assert.Equal("Console.WriteLine(\n", host.Document.Text);
        Assert.Equal("\"hi\");", host.InlineSuggestion);

        // Typing something else dismisses it; moving the caret does too.
        host.Editor.TextArea.PerformTextInput("x");
        Assert.Null(host.InlineSuggestion);
        host.Editor.Undo();
        Press(window, Key.OemPipe, PhysicalKey.Backslash, RawInputModifiers.Alt);
        await WaitUntil(() => host.InlineSuggestion is not null, TimeSpan.FromSeconds(5));
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Null(host.InlineSuggestion);
        Assert.Equal("Console.WriteLine".Length, host.Editor.CaretOffset);

        // None is asked for in the middle of a line's text.
        var asked = module.Requests.Count;
        host.Editor.CaretOffset = 3;
        Assert.False(await host.RequestInlineCompletionAsync());
        Assert.Equal(asked, module.Requests.Count);
        window.Close();
    }

    [Fact]
    public void A_suggestion_fits_its_place()
    {
        // The line's start written again, its indentation, the bracket that closes the line: they are there already.
        Assert.Equal("Line(x);", InlineSuggestions.Fit("Console.WriteLine(x);", "    Console.Write", ""));
        Assert.Equal("return x;", InlineSuggestions.Fit("    return x;", "    ", ""));
        Assert.Equal("a, b", InlineSuggestions.Fit("a, b)", "f(", ")"));
        Assert.Equal("one\ntwo", InlineSuggestions.Fit("one\r\ntwo  \n\n", "", ""));
        Assert.Null(InlineSuggestions.Fit(")", "f(", ")"));
        Assert.Null(InlineSuggestions.Fit("  \n ", "", ""));
        Assert.True(InlineSuggestions.FitsBefore(");"));
        Assert.True(InlineSuggestions.FitsBefore(""));
        Assert.False(InlineSuggestions.FitsBefore("bar"));
    }
}
