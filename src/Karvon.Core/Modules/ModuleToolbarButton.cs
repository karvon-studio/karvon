using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Modules;

/// <summary>
/// A module's button in the toolbar, at its right before Settings (Claude's Telegram remote control): an icon, a tooltip
/// that says how it is, a green dot while it is on, dimmed while it is off; a click does what it says, or opens its menu.
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

    /// <summary>What it stands for is off, or not set up: the icon is dimmed.</summary>
    [ObservableProperty]
    public partial bool IsDimmed { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>What a click does, when it has no menu.</summary>
    public Func<Task>? Click { get; set; }

    /// <summary>What a click opens instead: its menu.</summary>
    public IReadOnlyList<ModuleToolbarMenuItem>? Menu { get; init; }

    public bool HasMenu => Menu is { Count: > 0 };

    [RelayCommand]
    Task Activate() => Click?.Invoke() ?? Task.CompletedTask;
}

/// <summary>An item of a toolbar button's menu: what it says, ticked where it is a switch, and what it does.</summary>
public sealed partial class ModuleToolbarMenuItem(string header, Func<Task> run) : ObservableObject
{
    public string Header { get; } = header;

    /// <summary>A switch: ticked while it is on.</summary>
    public bool IsCheckable { get; init; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [RelayCommand]
    Task Run() => run();
}
