using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Postman.ViewModels;

namespace Karvon.Postman.Views;

/// <summary>A run's view (see <see cref="PostmanRunDocument"/>): a double click on a result opens its request.</summary>
public partial class PostmanRunView : UserControl
{
    public PostmanRunView() => InitializeComponent();

    void OnResultDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as PostmanRunDocument)?.OpenCommand.Execute(ResultList.SelectedItem);
}
