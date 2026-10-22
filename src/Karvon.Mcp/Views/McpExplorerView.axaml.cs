using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Mcp.ViewModels;
using Karvon.Ui;

namespace Karvon.Mcp.Views;

/// <summary>The MCP Servers tool window (see <see cref="McpExplorerViewModel"/>): double-click or Enter opens a row's page.</summary>
public partial class McpExplorerView : UserControl
{
    public McpExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (Open(source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext))
            e.Handled = true;
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Open(Tree.SelectedItem))
            e.Handled = true;
    }

    static bool Open(object? row)
    {
        switch (row)
        {
            case McpServerNode server:
                server.OpenCommand.Execute(null);
                return true;
            case McpItemNode item:
                item.OpenCommand.Execute(null);
                return true;
            case McpBuiltInToolNode tool:
                tool.OpenCommand.Execute(null);
                return true;
            case McpGroupNode { IsBuiltIn: true } group:
                group.OpenCommand.Execute(null);
                return true;
        }
        return false;
    }

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);
}
