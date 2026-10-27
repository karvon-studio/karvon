using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.ViewModels;

/// <summary>
/// Hot Reload, as Visual Studio's: a run whose language applies code changes while it runs (Ctrl+F5 of a .NET project,
/// under dotnet watch) gets the changes as they are saved, and Debug > Hot Reload (Alt+F10) saves them to apply them
/// now. What it did is said in the status bar.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Debug > Enable Hot Reload (kept in the settings); it counts from the next run.</summary>
    public bool HotReload
    {
        get => Settings.HotReload;
        set
        {
            if (value == Settings.HotReload)
                return;
            Settings.HotReload = value;
            Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>The program running applies code changes as they are saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyHotReloadCommand))]
    public partial bool IsHotReloading { get; set; }

    /// <summary>Debug > Hot Reload: the documents with changes saved, which the running program applies.</summary>
    [RelayCommand(CanExecute = nameof(IsHotReloading))]
    void ApplyHotReload()
    {
        if (!Editors.Any(d => d.IsDirty))
        {
            SetStatus("Hot Reload: no changes to apply (every document is saved)", StatusKind.Running);
            return;
        }
        SaveAll();
        SetStatus("Hot Reload: applying the changes...", StatusKind.Running);
    }
}
