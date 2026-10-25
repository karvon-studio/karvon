using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Karvon.Git;

/// <summary>An interactive rebase's commits (see <see cref="GitRebaseViewModel"/>), a dropped one struck through.</summary>
public partial class GitRebaseView : UserControl
{
    public static readonly IValueConverter Strike = new FuncValueConverter<bool, TextDecorationCollection?>(dropped => dropped ? TextDecorations.Strikethrough : null);

    public GitRebaseView() => InitializeComponent();
}
