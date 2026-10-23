using System.ComponentModel;
using Karvon.Updates;

namespace Karvon.ViewModels;

/// <summary>Karvon Studio's update, as the status bar says it (as Visual Studio's notifications do): available, downloading, ready.</summary>
public partial class MainViewModel
{
    Updater? _updates;

    /// <summary>The updater the window shows (the application's; the tests' own).</summary>
    public Updater Updates
    {
        get
        {
            if (_updates is null)
                Updates = AppUpdates.Current;
            return _updates!;
        }
        set
        {
            if (_updates is not null)
                _updates.PropertyChanged -= OnUpdatesChanged;
            _updates = value;
            _updates.PropertyChanged += OnUpdatesChanged;
            OnUpdatesChanged(null, new PropertyChangedEventArgs(null));
        }
    }

    void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(UpdateBadge));
        OnPropertyChanged(nameof(HasUpdateBadge));
    });

    /// <summary>The status bar's word on the update; null when there is none to say.</summary>
    public string? UpdateBadge => _updates is not { } updates ? null : updates.State switch
    {
        UpdateState.Available => $"Karvon Studio {updates.Latest?.Version} is available",
        UpdateState.Downloading => $"Downloading {updates.Latest?.Version}: {updates.Progress:P0}",
        UpdateState.Ready => "Restart to update",
        _ => null,
    };

    public bool HasUpdateBadge => UpdateBadge is not null;
}
