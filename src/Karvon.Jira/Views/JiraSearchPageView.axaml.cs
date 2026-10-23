using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Jira.ViewModels;

namespace Karvon.Jira.Views;

/// <summary>A JQL search's page (see <see cref="JiraSearchPageViewModel"/>): Enter searches, a double click opens an issue.</summary>
public partial class JiraSearchPageView : UserControl
{
    public JiraSearchPageView()
    {
        InitializeComponent();
        Results.AddHandler(DoubleTappedEvent, (_, _) => (DataContext as JiraSearchPageViewModel)?.OpenCommand.Execute(null), RoutingStrategies.Bubble);
        Results.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
                (DataContext as JiraSearchPageViewModel)?.OpenCommand.Execute(null);
        }, RoutingStrategies.Tunnel);
        JqlBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = (DataContext as JiraSearchPageViewModel)?.SearchCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
