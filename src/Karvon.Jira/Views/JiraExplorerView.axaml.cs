using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Karvon.Jira.ViewModels;

namespace Karvon.Jira.Views;

/// <summary>The Jira tool window (see <see cref="JiraExplorerViewModel"/>): a double click or Enter opens an issue, a board, a query.</summary>
public partial class JiraExplorerView : UserControl
{
    public JiraExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    JiraExplorerViewModel? Vm => DataContext as JiraExplorerViewModel;

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is JiraNode { Kind: JiraNodeKind.Issue or JiraNodeKind.Board } node)
        {
            Vm?.Open(node);
            e.Handled = true;
        }
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is JiraNode node)
        {
            Vm?.Open(node);
            e.Handled = true;
        }
    }
}

/// <summary>How the Jira views show what they show.</summary>
public static class JiraConverters
{
    public static readonly IValueConverter Bold = new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter Muted = new FuncValueConverter<bool, IBrush>(muted => muted ? new SolidColorBrush(Color.Parse("#9D9D9D")) : new SolidColorBrush(Color.Parse("#F1F1F1")));

    /// <summary>A secret box's hint: an edited site's secret stays unless a new one is typed.</summary>
    public static readonly IValueConverter SecretPlaceholder = new FuncValueConverter<bool, string>(known => known ? "Unchanged (type a new one to replace it)" : "");

    /// <summary>Test connection's word: red when it failed, green when it signed in.</summary>
    public static readonly IValueConverter MessageBrush = new FuncValueConverter<bool, IBrush>(failed => new SolidColorBrush(Color.Parse(failed ? "#F14C4C" : "#3FB950")));

    /// <summary>An issue's status category's color: to do gray, in progress blue, done green.</summary>
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<string?, IBrush>(category => new SolidColorBrush(Color.Parse(category switch
    {
        "indeterminate" => "#2D5A8A",
        "done" => "#2B5E36",
        _ => "#4A4A50",
    })));
}
