using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The tests' fake language server for .hint files, giving inlay hints, code lenses and a Fix All.</summary>
sealed class FakeHintsModule : KarvonModule
{
    public override string Id => "fake-hints";

    public override string Name => "Fake hints";

    public override string Description => "A language server with hints for the tests.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("fake-hints", "Fake hints server", path => path.EndsWith(".hint", StringComparison.Ordinal), start => FakeLanguageClient.StartAsync(start, null, "--hints"))];
}

/// <summary>The editor's multiple carets, sticky scroll, minimap, inlay hints, CodeLens and Fix All.</summary>
public class EditorFeaturesTests
{
    /// <summary>A folder with a file in it (a folder with none is no workspace), opened.</summary>
    static async Task<(MainViewModel Vm, Window Window, string File)> OpenFolderAsync(string name, string text, params KarvonModule[] more)
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, text);
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), .. more]);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        return (vm, window, file);
    }

    [AvaloniaFact]
    public async Task Several_carets_type_and_delete_at_once()
    {
        var (vm, window, file) = await OpenFolderAsync("notes.txt", "foo bar foo baz foo\n");
        var host = vm.OpenDocument(file)!.Host;
        host.Editor.CaretOffset = 1;

        // Shift+Alt+.: the word at the caret, then its next match; Shift+Alt+;: every match.
        host.AddNextMatchCaret();
        Assert.Equal([(3, 0), (11, 8)], host.Carets);
        host.AddAllMatchCarets();
        Assert.Equal(3, host.Carets.Count);
        await WaitUntil(() => host.Editor.TextArea.IsEffectivelyVisible && TopLevel.GetTopLevel(host.Editor) is not null, TimeSpan.FromSeconds(5));
        host.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        void Press(Key key, PhysicalKey physical) => window.KeyPress(key, RawInputModifiers.None, physical, null);
        host.Editor.TextArea.PerformTextInput("qux");
        Assert.Equal("qux bar qux baz qux\n", host.Document.Text);
        Assert.True(host.Editor.TextArea.IsKeyboardFocusWithin, "the editor has the focus");
        Assert.Equal(3, host.Carets.Count);
        Press(Key.Back, PhysicalKey.Backspace);
        Assert.Equal("qu bar qu baz qu\n", host.Document.Text);
        // Home: they come to the line's start, one caret there.
        Press(Key.Home, PhysicalKey.Home);
        Assert.Equal(1, host.Carets.Count);
        host.Editor.TextArea.PerformTextInput(">");
        Assert.Equal(">qu bar qu baz qu\n", host.Document.Text);

        // Ctrl+Alt+click's: a caret more where it is; Escape leaves the main one.
        host.ClearExtraCarets();
        host.Editor.CaretOffset = 0;
        host.AddCaret(host.Document.TextLength - 1);
        host.Editor.TextArea.PerformTextInput("!");
        Assert.Equal("!>qu bar qu baz qu!\n", host.Document.Text);
        Press(Key.Escape, PhysicalKey.Escape);
        Assert.False(host.HasExtraCarets);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quick_info_shows_a_long_diagnostic_whole()
    {
        var (vm, window, file) = await OpenFolderAsync("drive.py", "from google.auth.transport.requests import Request\n");
        var view = vm.OpenDocument(file)!.Host.PrimaryView;
        var text = "Import \"google.auth.transport.requests\" could not be resolved from the source of the virtual environment's site-packages";
        var diagnostic = new Karvon.Lsp.LspDiagnostic(new(new(0, 5), new(0, 37)), Karvon.Lsp.DiagnosticSeverity.Error, "reportMissingImports", text, false);

        // The editor's tooltips may be as wide as Quick Info (the theme's stop at 320 pixels).
        Assert.Equal(Karvon.Editor.QuickInfo.MaxWidth, view.Resources["ToolTipContentMaxWidth"]);

        // Quick Info in a tooltip as the theme has it and as the editor has it: the message wrapped in it, never cut off.
        (ToolTip Tip, Control Info) Tip(bool editors)
        {
            var info = Karvon.Editor.QuickInfo.Build(null, [diagnostic]);
            var host = new Border { Child = new ToolTip { Content = info, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left } };
            if (editors)
                host.Resources["ToolTipContentMaxWidth"] = Karvon.Editor.QuickInfo.MaxWidth;
            return ((ToolTip)host.Child, info);
        }
        var (theme, editor) = (Tip(false), Tip(true));
        var tips = new Window { Width = 1200, Height = 500, Content = new StackPanel { Spacing = 20, Children = { (Control)theme.Tip.Parent!, (Control)editor.Tip.Parent! } } };
        tips.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var (_, info) in new[] { theme, editor })
        {
            var message = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(info).OfType<TextBlock>().Single(t => t.Text == text);
            Assert.True(message.Bounds.Right <= ((Control)message.Parent!).Bounds.Width + 0.5, $"the message ends at {message.Bounds.Right}, its row at {((Control)message.Parent!).Bounds.Width}");
            Assert.True(message.Bounds.Height > message.FontSize * 1.8, "the message wraps");
        }
        Assert.True(theme.Info.Bounds.Width <= 320, $"the theme's is {theme.Info.Bounds.Width} wide");
        Assert.True(editor.Info.Bounds.Width > 400, $"the editor's is {editor.Info.Bounds.Width} wide");
        tips.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sticky_scroll_keeps_the_scopes_lines_and_the_minimap_shows_the_file()
    {
        var body = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"      step{i}();"));
        var (vm, window, file) = await OpenFolderAsync("app.js", $"function outer() {{\n  if (ready) {{\n    for (const x of xs) {{\n{body}\n    }}\n  }}\n}}\n");
        var doc = vm.OpenDocument(file)!;
        var view = doc.Host.PrimaryView;
        await WaitUntil(() => doc.Host.Outlines.Count >= 3 && view.TextArea.TextView.VisualLinesValid, TimeSpan.FromSeconds(10));
        Assert.Empty(doc.Host.StickyLines(view));

        view.ScrollToLine(40);
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => doc.Host.StickyLines(view).Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal([1, 2, 3], doc.Host.StickyLines(view));

        vm.Minimap = true;
        Assert.True(doc.Host.ShowMinimap);
        Assert.Equal(90, view.Margin.Right);
        Snapshot(window, "editor-sticky-minimap");
        vm.Minimap = false;
        Assert.Equal(0, view.Margin.Right);
        vm.StickyScroll = false;
        Assert.Empty(doc.Host.StickyLines(view));
        vm.StickyScroll = true;
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_servers_inlay_hints_CodeLens_and_Fix_All_show_in_the_editor()
    {
        var (vm, window, file) = await OpenFolderAsync("plan.hint", "call(42)\ndef thing\nTODO one\nTODO two\n", new FakeHintsModule());
        var host = vm.OpenDocument(file)!.Host;

        await WaitUntil(() => host.InlayHints.Count == 1 && host.CodeLenses.Count == 1, TimeSpan.FromSeconds(15));
        Assert.Equal((5, "value:"), host.InlayHints[0]);
        Assert.Equal((2, "2 references"), host.CodeLenses[0]);
        Snapshot(window, "editor-hints");

        // The hint keeps its place as the line is edited before it.
        host.Document.Insert(0, "  ");
        Assert.Equal(7, host.InlayHints[0].Offset);

        // Ctrl+.: a Fix All asks in what scope, then fixes every place there.
        var menu = (await host.ShowQuickActionsAsync())!;
        var fixAll = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Fix all TODOs");
        var scopes = fixAll.ItemsSource!.Cast<MenuItem>().ToList();
        Assert.Equal(["Fix all occurrences in Document", "Fix all occurrences in Project", "Fix all occurrences in Solution"], scopes.Select(s => s.Header as string));
        menu.Close();
        scopes[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => host.Document.Text.Contains("DONE one") && host.Document.Text.Contains("DONE two"), TimeSpan.FromSeconds(10));

        vm.InlayHints = false;
        await WaitUntil(() => host.InlayHints.Count == 0, TimeSpan.FromSeconds(10));
        vm.InlayHints = true;
        window.Close();
    }
}
