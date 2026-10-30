using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Karvon.Updates;

namespace Karvon.Views;

/// <summary>
/// Karvon Studio's update as a notification in the window's corner, as Visual Studio's: the newer release the check
/// found as it started, its download, then Restart and update. Closed, it is not shown again while Karvon Studio runs
/// (the status bar still says it).
/// </summary>
public sealed class UpdateNotification : Border
{
    readonly Updater _updater;
    readonly TextBlock _title;
    readonly TextBlock _text;
    readonly Button _update;

    /// <param name="restart">Restart and update: Karvon Studio closes, the update installs, and it starts again.</param>
    /// <param name="notes">Release notes: the release's notes shown.</param>
    public UpdateNotification(Updater updater, Action restart, Action notes)
    {
        _updater = updater;
        Name = "UpdateNotification";
        Width = 380;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Background = Brush("VsMenuBrush", "#1B1B1C");
        BorderBrush = Brush("VsAccentBrush", "#007ACC");
        BorderThickness = new Thickness(3, 1, 1, 1);
        Padding = new Thickness(12, 10);
        BoxShadow = BoxShadows.Parse("0 2 10 0 #80000000");
        IsVisible = false;

        _title = new TextBlock { Name = "UpdateNotificationTitle", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock { Name = "UpdateNotificationText", TextWrapping = TextWrapping.Wrap, Foreground = Brush("VsGrayTextBrush", "#9D9D9D") };
        _update = new Button { Name = "UpdateNotificationUpdate", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var notesButton = new Button { Name = "UpdateNotificationNotes", Content = "Release notes", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var close = new Button
        {
            Name = "UpdateNotificationClose", Classes = { "tbi" }, Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Top,
            Content = new VsIcon { Kind = "Close", Width = 12, Height = 12 },
        };
        ToolTip.SetTip(close, "Close");
        _update.Click += async (_, _) =>
        {
            if (updater.State == UpdateState.Available)
                await updater.DownloadAsync();
            else if (updater.State == UpdateState.Ready)
                restart();
        };
        notesButton.Click += (_, _) => notes();
        close.Click += (_, _) =>
        {
            updater.Dismissed = updater.Latest?.Version;
            Refresh();
        };

        var top = new DockPanel { Children = { Right(close), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new VsIcon { Kind = "Pull", Width = 16, Height = 16 }, _title } } } };
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                top,
                _text,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { notesButton, _update } },
            },
        };
        AttachedToVisualTree += (_, _) =>
        {
            updater.PropertyChanged += OnUpdaterChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => updater.PropertyChanged -= OnUpdaterChanged;
    }

    void OnUpdaterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Shown while there is an update to say (available, downloading, ready) and it was not closed.</summary>
    void Refresh()
    {
        var version = _updater.Latest?.Version;
        IsVisible = _updater.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready && version != _updater.Dismissed;
        if (!IsVisible)
            return;
        (_title.Text, _text.Text) = _updater.State switch
        {
            UpdateState.Available => ($"Karvon Studio {version} is available", "Update downloads it; then Restart and update installs it."),
            UpdateState.Downloading => ($"Karvon Studio {version} is available", $"Downloading: {_updater.Progress:P0}"),
            _ => ($"Karvon Studio {version} is ready", "Downloaded: Restart and update installs it now."),
        };
        _update.Content = _updater.State == UpdateState.Ready ? "Restart and update" : "Update";
        _update.IsVisible = _updater.State != UpdateState.Downloading;
    }

    static Control Right(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    static IBrush Brush(string key, string fallback) =>
        Application.Current?.TryFindResource(key, out var found) == true && found is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
