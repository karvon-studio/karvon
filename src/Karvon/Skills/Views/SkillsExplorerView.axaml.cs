using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Docker.Views;
using Karvon.Skills.ViewModels;

namespace Karvon.Skills.Views;

/// <summary>The Skills tool window (see <see cref="SkillsExplorerViewModel"/>): double-click or Enter opens a row.</summary>
public partial class SkillsExplorerView : UserControl
{
    public SkillsExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
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
        var command = row switch
        {
            SkillNode skill => skill.OpenCommand,
            SkillsFileNode file => file.OpenCommand,
            SkillsMarketplaceNode marketplace => marketplace.OpenCommand,
            _ => null,
        };
        command?.Execute(null);
        return command is not null;
    }

    void OnCopy(object? sender, RoutedEventArgs e) => DockerViews.Copy(this, (sender as Control)?.Tag as string);
}
