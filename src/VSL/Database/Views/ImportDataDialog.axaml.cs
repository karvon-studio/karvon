namespace VSL.Database.Views;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VSL.Database.Controls;
using VSL.Database.ViewModels;

/// <summary>The data import dialog; returns <c>true</c> when rows were imported.</summary>
public partial class ImportDataDialog : Window
{
    /// <summary>Creates the dialog.</summary>
    public ImportDataDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImportDataViewModel vm)
            {
                vm.Closed += imported => Close(imported);
                vm.PreviewChanged += () => GridColumns.Build(this.FindControl<DataGrid>("Preview")!, vm.PreviewColumns, []);
            }
        };
    }
}
