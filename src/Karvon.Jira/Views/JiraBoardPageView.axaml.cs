using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Jira.ViewModels;

namespace Karvon.Jira.Views;

/// <summary>
/// A board's page (see <see cref="JiraBoardPageViewModel"/>): a card dragged to another column moves its issue there (by
/// the transition to that column's status); a double click opens it.
/// </summary>
public partial class JiraBoardPageView : UserControl
{
    (JiraIssueSummary Issue, JiraColumn From)? _dragged;

    public JiraBoardPageView()
    {
        InitializeComponent();
        Columns.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        Columns.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        Columns.AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
    }

    JiraBoardPageViewModel? Vm => DataContext as JiraBoardPageViewModel;

    static (JiraIssueSummary, JiraColumn)? CardAt(Visual? visual) =>
        visual?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("card")
            && border.Tag is JiraIssueSummary issue && ColumnAt(border) is { } column
            ? (issue, column)
            : null;

    static JiraColumn? ColumnAt(Visual? visual)
    {
        for (var v = visual; v is not null; v = v.GetVisualParent())
            if (v is Control { DataContext: JiraColumn column })
                return column;
        return null;
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            _dragged = CardAt(e.Source as Visual);
    }

    async void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is not { } dragged || Vm is not { } vm)
            return;
        _dragged = null;
        var target = ColumnAt(Columns.InputHitTest(e.GetPosition(Columns)) as Visual);
        if (target is not null && target != dragged.From)
            await vm.MoveAsync(dragged.Issue, target);
    }

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CardAt(e.Source as Visual) is { } card)
            Vm?.OpenCommand.Execute(card.Item1);
    }
}
