using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>The document well as in Visual Studio: tab groups, dragging tabs, floating windows and Window > Split.</summary>
public class DocumentWellTests
{
    static async Task<(MainViewModel Vm, MainWindow Window, string Dir)> OpenAsync()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        return (vm, window, Path.GetDirectoryName(sln)!);
    }

    static DocumentGroupView[] GroupViews(Window window) => window.GetVisualDescendants().OfType<DocumentGroupView>().ToArray();

    static Point CenterOf(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), relativeTo)!.Value;

    static TabItem TabOf(Window window, DocumentTab doc) =>
        window.GetVisualDescendants().OfType<TabItem>().Single(t => t.DataContext == doc || (t.Content as DocumentTab) == doc);

    static async Task Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(12, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(50));
        window.MouseUp(to, MouseButton.Left);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
    }

    [AvaloniaFact]
    public async Task Tab_groups_split_the_well_and_follow_their_documents()
    {
        var (vm, window, dir) = await OpenAsync();
        var program = vm.OpenDocument(Path.Combine(dir, "Web", "Program.cs"))!;
        var settings = vm.OpenDocument(Path.Combine(dir, "Web", "appsettings.json"))!;
        var greeting = vm.OpenDocument(Path.Combine(dir, "Components", "Greeting.razor.cs"))!;
        Assert.Single(vm.DocumentGroups);
        Assert.Equal([program, settings, greeting], vm.DocumentGroups[0].Tabs);

        // New Vertical Tab Group: the document moves into a group of its own to the right and is active there.
        Assert.True(vm.CanCreateTabGroup(greeting, vertical: true));
        vm.NewVerticalTabGroupCommand.Execute(greeting);
        Assert.Equal(2, vm.DocumentGroups.Count);
        Assert.Equal([greeting], vm.DocumentGroups[1].Tabs);
        Assert.Equal(greeting, vm.ActiveDocument);
        Assert.Same(vm.DocumentGroups[1], vm.ActiveGroup);
        await WaitUntil(() => GroupViews(window).Length == 2, TimeSpan.FromSeconds(5));
        var views = GroupViews(window);
        Assert.True(views[1].Bounds.X > views[0].Bounds.X, "the new group is to the right");
        Assert.Same(views[1], greeting.Host.View.FindAncestorOfType<DocumentGroupView>());
        // The groups are side by side now: no horizontal group until they are one again.
        Assert.False(vm.CanCreateTabGroup(settings, vertical: false));

        // A click into the first group makes it the active one, with its own document in front.
        window.MouseDown(CenterOf(views[0], window), MouseButton.Left);
        window.MouseUp(CenterOf(views[0], window), MouseButton.Left);
        Assert.Same(vm.DocumentGroups[0], vm.ActiveGroup);
        Assert.Equal(vm.DocumentGroups[0].Active, vm.ActiveDocument);

        // Move to Next Tab Group, then closing the second group's documents removes it.
        vm.MoveToNextTabGroupCommand.Execute(settings);
        Assert.Equal([greeting, settings], vm.DocumentGroups[1].Tabs);
        await vm.CloseDocumentCommand.ExecuteAsync(settings);
        Assert.Equal(greeting, vm.ActiveDocument);
        await vm.CloseDocumentCommand.ExecuteAsync(greeting);
        Assert.Single(vm.DocumentGroups);
        Assert.Equal(program, vm.ActiveDocument);
        // One tab cannot make a group of its own.
        Assert.False(vm.CanCreateTabGroup(program, vertical: false));
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tabs_are_dragged_to_reorder_and_to_a_new_group()
    {
        var (vm, window, dir) = await OpenAsync();
        var program = vm.OpenDocument(Path.Combine(dir, "Web", "Program.cs"))!;
        var settings = vm.OpenDocument(Path.Combine(dir, "Web", "appsettings.json"))!;
        var greeting = vm.OpenDocument(Path.Combine(dir, "Components", "Greeting.razor.cs"))!;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));

        // Dragged onto the first tab's left half: it goes first.
        var first = TabOf(window, program);
        var to = first.TranslatePoint(new Point(4, first.Bounds.Height / 2), window)!.Value;
        await Drag(window, CenterOf(TabOf(window, greeting), window), to);
        Assert.Equal([greeting, program, settings], vm.DocumentGroups[0].Tabs);
        Assert.Equal(greeting, vm.ActiveDocument);

        // Dropped on the right edge of the document: a new vertical group with that tab.
        var view = GroupViews(window).Single();
        var edge = view.TranslatePoint(new Point(view.Bounds.Width - 30, view.Bounds.Height / 2), window)!.Value;
        await Drag(window, CenterOf(TabOf(window, settings), window), edge);
        Assert.Equal(2, vm.DocumentGroups.Count);
        Assert.True(vm.DocumentGroupsVertical);
        Assert.Equal([settings], vm.DocumentGroups[1].Tabs);
        Snapshot(window, "tab-groups");

        // Dropped on the middle of the first group: it joins that group again and the empty group goes away.
        await WaitUntil(() => GroupViews(window).Length == 2, TimeSpan.FromSeconds(5));
        var left = GroupViews(window)[0];
        await Drag(window, CenterOf(TabOf(window, settings), window), CenterOf(left, window));
        Assert.Single(vm.DocumentGroups);
        Assert.Equal([greeting, program, settings], vm.DocumentGroups[0].Tabs);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_document_floats_in_its_own_window_and_docks_back()
    {
        var (vm, window, dir) = await OpenAsync();
        var program = vm.OpenDocument(Path.Combine(dir, "Web", "Program.cs"))!;
        var greeting = vm.OpenDocument(Path.Combine(dir, "Components", "Greeting.razor.cs"))!;

        vm.FloatDocumentCommand.Execute(greeting);
        var floating = Assert.Single(vm.FloatingGroups);
        Assert.Equal([greeting], floating.Tabs);
        Assert.Equal([program], vm.DocumentGroups[0].Tabs);
        var popup = Assert.Single(window.OwnedWindows.OfType<DocumentWindow>());
        Assert.True(popup.IsVisible);
        Assert.Equal("Greeting.razor.cs - VSL", popup.Title);
        // Its editor lives in the floating window and edits the same document.
        await WaitUntil(() => TopLevel.GetTopLevel(greeting.Host.View) == popup, TimeSpan.FromSeconds(5));
        greeting.Host.Document.Insert(0, "// floating\n");
        Assert.True(greeting.IsDirty);
        Assert.Equal(greeting, vm.ActiveDocument);

        // Dock as Tabbed Document: back in the main window, and the floating window closes.
        vm.DockDocumentCommand.Execute(greeting);
        Assert.Empty(vm.FloatingGroups);
        Assert.Equal([program, greeting], vm.DocumentGroups[0].Tabs);
        await WaitUntil(() => !popup.IsVisible, TimeSpan.FromSeconds(5));
        await WaitUntil(() => TopLevel.GetTopLevel(greeting.Host.View) == window, TimeSpan.FromSeconds(5));
        greeting.Host.Editor.Undo();
        Assert.False(greeting.IsDirty);

        // Closing a floating window closes its documents.
        vm.FloatDocumentCommand.Execute(program);
        var second = Assert.Single(window.OwnedWindows.OfType<DocumentWindow>(), w => w.IsVisible);
        second.Close();
        await WaitUntil(() => vm.Documents.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Empty(vm.FloatingGroups);
        Assert.Equal([greeting], vm.Documents);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_split_shows_the_document_in_two_panes()
    {
        var (vm, window, dir) = await OpenAsync();
        var doc = vm.OpenDocument(Path.Combine(dir, "Web", "Program.cs"))!;
        var host = doc.Host;
        host.Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(3, 1);

        host.ToggleSplit();
        Assert.True(host.IsSplit);
        var second = host.SecondView!;
        Assert.Same(host.Document, second.Document);
        Assert.Equal(3, second.TextArea.Caret.Line);
        await WaitUntil(() => second.IsEffectivelyVisible && second.Bounds.Height > 50, TimeSpan.FromSeconds(5));
        Assert.True(second.Bounds.Height > 50 && host.PrimaryView.Bounds.Height > 50);
        Assert.True(second.TranslatePoint(default, window)!.Value.Y < host.PrimaryView.TranslatePoint(default, window)!.Value.Y);
        Snapshot(window, "split");

        // Clicking into the top pane makes it the one commands and the status bar follow; text typed there is the document's.
        window.MouseDown(CenterOf(second, window), MouseButton.Left);
        window.MouseUp(CenterOf(second, window), MouseButton.Left);
        Assert.Same(second, host.Editor);
        second.Document.Insert(0, "// split\n");
        Assert.StartsWith("// split", host.PrimaryView.Text);
        second.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(2, 1);
        Assert.Equal("Ln: 2   Ch: 1", vm.CaretText);

        // Remove Split: one pane again, with the caret of the pane the user was in.
        host.ToggleSplit();
        Assert.False(host.IsSplit);
        Assert.Same(host.PrimaryView, host.Editor);
        Assert.Equal(2, host.Editor.TextArea.Caret.Line);
        host.Editor.Undo();
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tool_windows_float_and_dock_back()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();

        // Float: Solution Explorer in a window of its own, still the same tool window.
        window.FloatToolWindow(ToolWindows.SolutionExplorer);
        Assert.True(window.IsFloating(ToolWindows.SolutionExplorer));
        Assert.Null(window.SideOf(ToolWindows.SolutionExplorer));
        var floating = window.FloatingWindowOf(ToolWindows.SolutionExplorer)!;
        Assert.True(floating.IsVisible);
        Assert.Equal("Solution Explorer", floating.Title);
        Assert.Equal("Float", vm.Settings.ToolLayout[ToolWindows.SolutionExplorer]);
        vm.ShowToolWindow(ToolWindows.SolutionExplorer);
        Assert.True(window.IsToolWindowShowing(ToolWindows.SolutionExplorer));

        // Closing its window docks it back where it was.
        floating.Close();
        Assert.Equal(DockSide.Right, window.SideOf(ToolWindows.SolutionExplorer));
        Assert.False(floating.IsVisible);
        Assert.Equal("Right", vm.Settings.ToolLayout[ToolWindows.SolutionExplorer]);

        // A tab dragged out of the window floats there.
        var tab = window.GetVisualDescendants().OfType<TabItem>().Single(t => (string?)t.Tag == ToolWindows.Output);
        var start = CenterOf(tab, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(20, -20), RawInputModifiers.LeftMouseButton);
        var outside = new Point(window.Bounds.Width + 80, 200);
        window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
        window.MouseUp(outside, MouseButton.Left);
        await WaitUntil(() => window.IsFloating(ToolWindows.Output), TimeSpan.FromSeconds(5));
        Assert.Equal(ToolWindows.Output, (window.FloatingWindowOf(ToolWindows.Output)!.Tabs.SelectedItem as TabItem)?.Tag);

        // It floats again in the next window, where it was.
        window.Close();
        await WaitUntil(() => !window.IsVisible, TimeSpan.FromSeconds(10));
        Assert.True(vm.Settings.Layout.FloatingTools.ContainsKey(ToolWindows.Output));
        var next = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        next.Show();
        await WaitUntil(() => next.IsFloating(ToolWindows.Output), TimeSpan.FromSeconds(5));
        Assert.Equal(DockSide.Right, next.SideOf(ToolWindows.SolutionExplorer));
        next.ResetWindowLayout();
        Assert.Equal(DockSide.Bottom, next.SideOf(ToolWindows.Output));
        Assert.False(next.IsFloating(ToolWindows.Output));
        next.Close();
    }
}
