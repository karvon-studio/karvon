using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Modules;

/// <summary>
/// A module's button in the toolbar, at its right before Settings (Claude's Telegram remote control): an icon, a tooltip
/// that says how it is, a green dot while it is on, and what a click does.
/// </summary>
public sealed partial class ModuleToolbarButton(string icon) : ObservableObject
{
    /// <summary>A <c>VsIcon</c> kind.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = icon;

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    /// <summary>What it stands for is on now: a green dot shows it.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>What a click does.</summary>
    public Func<Task>? Click { get; set; }

    [RelayCommand]
    Task Activate() => Click?.Invoke() ?? Task.CompletedTask;
}
