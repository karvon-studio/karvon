using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>On a Mac, Cmd is where Linux and Windows have Ctrl; Ctrl+Tab and Ctrl+` stay Ctrl.</summary>
public class PlatformKeysTests
{
    [Fact]
    public void Ctrl_is_Cmd_on_a_Mac()
    {
        static string Mac(string gesture) => PlatformKeys.ForPlatform(KeyGesture.Parse(gesture), mac: true).ToString();
        Assert.Equal(KeyGesture.Parse("Meta+S").ToString(), Mac("Ctrl+S"));
        Assert.Equal(KeyGesture.Parse("Meta+Shift+B").ToString(), Mac("Ctrl+Shift+B"));
        Assert.Equal(KeyGesture.Parse("Ctrl+Tab").ToString(), Mac("Ctrl+Tab"));
        Assert.Equal(KeyGesture.Parse("Ctrl+OemTilde").ToString(), Mac("Ctrl+OemTilde"));
        Assert.Equal(KeyGesture.Parse("F5").ToString(), Mac("F5"));
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), PlatformKeys.ForPlatform(KeyGesture.Parse("Ctrl+S"), mac: false));
        if (!OperatingSystem.IsMacOS())
            Assert.Equal(KeyModifiers.Control, PlatformKeys.Command);
    }

    [AvaloniaFact]
    public void The_window_and_its_menus_take_Cmd_on_a_Mac()
    {
        var window = new MainWindow { DataContext = NewViewModel(), Width = 1280, Height = 800 };
        window.Show();
        PlatformKeys.Apply(window, mac: true);
        Assert.Contains(window.KeyBindings, b => b.Gesture is { Key: Key.S, KeyModifiers: KeyModifiers.Meta });
        Assert.Contains(window.KeyBindings, b => b.Gesture is { Key: Key.B, KeyModifiers: KeyModifiers.Meta | KeyModifiers.Shift });
        Assert.DoesNotContain(window.KeyBindings, b => b.Gesture is { } g && g.KeyModifiers.HasFlag(KeyModifiers.Control) && g.Key is not (Key.Tab or Key.OemTilde));
        var gestures = window.GetLogicalDescendants().OfType<MenuItem>().Select(i => i.InputGesture).OfType<KeyGesture>().ToList();
        Assert.NotEmpty(gestures);
        Assert.DoesNotContain(gestures, g => g.KeyModifiers.HasFlag(KeyModifiers.Control) && g.Key is not (Key.Tab or Key.OemTilde));
        window.Close();
    }
}
