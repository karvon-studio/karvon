using Avalonia.Controls;
using Avalonia.Input;
using VSL.Skills.ViewModels;

namespace VSL.Skills.Views;

/// <summary>A skill's page (see <see cref="SkillPageViewModel"/>): Enter in the prompt tries it.</summary>
public partial class SkillPageView : UserControl
{
    public SkillPageView() => InitializeComponent();

    void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SkillPageViewModel page)
        {
            page.TryCommand.Execute(null);
            e.Handled = true;
        }
    }
}
