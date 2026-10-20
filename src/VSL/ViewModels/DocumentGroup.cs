using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VSL.ViewModels;

/// <summary>
/// A tab group of the document well (Window > New Vertical / Horizontal Tab Group), or the tabs of a floating document
/// window. Every open document is in exactly one group; <see cref="MainViewModel.Documents"/> lists them all.
/// </summary>
public sealed partial class DocumentGroup : ObservableObject
{
    public DocumentGroup(bool isFloating = false) => IsFloating = isFloating;

    public ObservableCollection<DocumentTab> Tabs { get; } = [];

    /// <summary>The tab in front of this group.</summary>
    [ObservableProperty]
    public partial DocumentTab? Active { get; set; }

    /// <summary>The group the keyboard is in: its selected tab is drawn in the accent color.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>In a window of its own instead of the main window's document well.</summary>
    public bool IsFloating { get; }
}
