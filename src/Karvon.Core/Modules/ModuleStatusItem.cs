using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Modules;

/// <summary>
/// A module's item in the status bar, before Git's (Pipelines: the branch's last run): an icon, a few words, what a
/// click does. Hidden until the module has something to say.
/// </summary>
public sealed partial class ModuleStatusItem : ObservableObject
{
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>A <c>VsIcon</c> kind, or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial string? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <summary>What a click does.</summary>
    public Action? Click { get; set; }

    [RelayCommand]
    void Activate() => Click?.Invoke();
}
