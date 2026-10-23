using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Karvon.Views;

/// <summary>
/// Keys as each desktop has them: what is Ctrl on Linux and Windows is Cmd on macOS (Cmd+S, Cmd+Shift+B, Cmd+click),
/// as in every Mac app and in VS Code there. Ctrl+Tab and Ctrl+` stay Ctrl on a Mac too (Cmd+Tab and Cmd+` are the
/// system's), as VS Code has them.
/// </summary>
public static class PlatformKeys
{
    /// <summary>The modifier of the commands: Cmd on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers Command => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Whether a key event has the command modifier (Ctrl, Cmd on macOS) among its modifiers.</summary>
    public static bool HasCommand(KeyModifiers modifiers) => modifiers.HasFlag(Command);

    /// <summary>A gesture written as on Linux ("Ctrl+Shift+B"), as this desktop has it.</summary>
    public static KeyGesture Parse(string gesture) => ForPlatform(KeyGesture.Parse(gesture), OperatingSystem.IsMacOS());

    /// <summary>A gesture as on a Mac when <paramref name="mac"/>: its Ctrl becomes Cmd, but for the keys that stay Ctrl.</summary>
    public static KeyGesture ForPlatform(KeyGesture gesture, bool mac)
    {
        if (!mac || !gesture.KeyModifiers.HasFlag(KeyModifiers.Control) || gesture.Key is Key.Tab or Key.OemTilde)
            return gesture;
        return new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
    }

    /// <summary>
    /// A window's keys as on a Mac when <paramref name="mac"/>: its key bindings, and the gestures its menus show (the
    /// main menu's, items and submenus).
    /// </summary>
    public static void Apply(Window window, bool mac)
    {
        if (!mac)
            return;
        foreach (var binding in window.KeyBindings)
            if (binding.Gesture is { } gesture)
                binding.Gesture = ForPlatform(gesture, mac);
        foreach (var item in window.GetLogicalDescendants().OfType<MenuItem>())
            Apply(item, mac);
    }

    static void Apply(MenuItem item, bool mac)
    {
        if (item.InputGesture is { } gesture)
            item.InputGesture = ForPlatform(gesture, mac);
        foreach (var child in item.Items.OfType<MenuItem>())
            Apply(child, mac);
    }
}
