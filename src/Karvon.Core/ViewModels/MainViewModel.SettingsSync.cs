using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Karvon.Services;

namespace Karvon.ViewModels;

/// <summary>
/// Settings Sync (Tools > Settings Sync): the preferences kept in a folder that syncs (Dropbox's, Syncthing's, a share's)
/// or in a secret GitHub gist (the Pipelines module's, through gh), and taken from there on the user's other machines.
/// Synced when Karvon starts, a moment after a synced setting changes, and when asked.
/// </summary>
public partial class MainViewModel
{
    static readonly TimeSpan SettingsSyncDelay = TimeSpan.FromSeconds(3);
    DispatcherTimer? _settingsSyncTimer;
    bool _syncingSettings;

    /// <summary>Where the settings can be kept: a folder, and the modules' (GitHub).</summary>
    public IReadOnlyList<SettingsSyncTarget> SettingsSyncTargets =>
    [
        new("folder", "A folder", "A folder another tool syncs between the machines (Dropbox, Syncthing, OneDrive, a share): karvon-settings.json in it.",
            state => new FolderSettingsStore(state.Location ?? throw new InvalidOperationException("Settings Sync has no folder."))),
        .. Modules.SelectMany(m => m.SettingsSyncTargets),
    ];

    public bool IsSettingsSyncOn => Settings.Sync is not null;

    void WatchSettingsSync()
    {
        // A synced setting changed: sent a moment after (several changes, once).
        Settings.Saved += () => Dispatcher.UIThread.Post(() =>
        {
            if (Settings.Sync is not { } state || _syncingSettings || state.Last == SettingsSync.Of(Settings).ToJsonString())
                return;
            _settingsSyncTimer ??= new DispatcherTimer(SettingsSyncDelay, DispatcherPriority.Background, async (_, _) =>
            {
                _settingsSyncTimer!.Stop();
                await SyncSettingsAsync();
            });
            _settingsSyncTimer.Stop();
            _settingsSyncTimer.Start();
        });
        // When Karvon starts: what the other machines changed comes.
        if (Settings.Sync is not null)
            Dispatcher.UIThread.Post(() => _ = SyncSettingsAsync(), DispatcherPriority.Background);
    }

    [RelayCommand]
    async Task TurnOnSettingsSync()
    {
        if (Dialogs is null)
            return;
        var targets = SettingsSyncTargets;
        var choice = targets.Count == 1 ? targets[0].Name
            : await Dialogs.PickAsync("Turn On Settings Sync", "Keep the settings in", [.. targets.Select(t => t.Name)], targets[^1].Name);
        if (targets.FirstOrDefault(t => t.Name == choice) is not { } target)
            return;
        var state = new SettingsSyncState { Target = target.Id };
        if (target.Id == "folder")
        {
            if (await Dialogs.PickFolderAsync("A folder to keep the settings in (Dropbox, Syncthing, a share)") is not { } folder)
                return;
            state.Location = folder;
        }
        Settings.Sync = state;
        if (!await SyncSettingsAsync(firstTime: true))
            Settings.Sync = null;
        Settings.Save();
        OnPropertyChanged(nameof(IsSettingsSyncOn));
    }

    [RelayCommand]
    Task SyncSettingsNow() => SyncSettingsAsync();

    [RelayCommand]
    void TurnOffSettingsSync()
    {
        Settings.Sync = null;
        Settings.Save();
        OnPropertyChanged(nameof(IsSettingsSyncOn));
        SetStatus("Settings Sync is off: the settings kept there stay there.");
    }

    /// <summary>
    /// The settings merged with those kept there (what changed here since the last sync stays here's, the rest comes
    /// from there), then put there when they differ. The first time, when both have settings that differ, the user
    /// chooses whose. False when it could not.
    /// </summary>
    internal async Task<bool> SyncSettingsAsync(bool firstTime = false)
    {
        if (Settings.Sync is not { } state || _syncingSettings)
            return false;
        if (SettingsSyncTargets.FirstOrDefault(t => t.Id == state.Target) is not { } target)
        {
            SetStatus($"Settings Sync keeps the settings in {state.Target}, which no module here offers (its module is off).");
            return false;
        }
        _syncingSettings = true;
        try
        {
            var store = target.Open(state);
            var theirs = await store.ReadAsync(CancellationToken.None);
            var mine = SettingsSync.Of(Settings);
            JsonObject merged;
            if (theirs is null)
                merged = mine;
            else if (firstTime && !SettingsSync.Same(mine, theirs.Settings))
            {
                var take = Dialogs is null ? true : await Dialogs.ConfirmAsync("Turn On Settings Sync",
                    $"{store.Name} has the settings of {(theirs.Machine.Length > 0 ? theirs.Machine : "another machine")} ({theirs.Written.LocalDateTime:g}), not this machine's. "
                    + "Use them here? No puts this machine's there in their place.");
                if (take is null)
                    return false;
                merged = take == true ? theirs.Settings : mine;
            }
            else
                merged = SettingsSync.Merge(state.Last is { } last ? JsonNode.Parse(last) as JsonObject : null, mine, theirs.Settings);
            if (!SettingsSync.Same(merged, mine))
            {
                SettingsSync.Apply(Settings, merged);
                OnSyncedSettingsApplied();
            }
            if (theirs is null || !SettingsSync.Same(merged, theirs.Settings))
                await store.WriteAsync(new SyncedSettings(merged, DateTimeOffset.Now, Environment.MachineName), CancellationToken.None);
            state.Last = merged.ToJsonString();
            state.LastSynced = DateTimeOffset.Now;
            Settings.Save();
            SetStatus($"Settings synced with {store.Name}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            SetStatus("Settings Sync failed: " + ex.Message);
            return false;
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    /// <summary>Settings came from another machine: what shows them follows.</summary>
    void OnSyncedSettingsApplied()
    {
        foreach (var name in new[] { nameof(InlayHints), nameof(CodeLens), nameof(StickyScroll), nameof(Minimap), nameof(LineBlame), nameof(MarkdownPreview) })
            OnPropertyChanged(name);
        Editor.EditorHost.DefaultZoom = Math.Clamp(Settings.EditorZoom, Editor.EditorHost.MinZoom, Editor.EditorHost.MaxZoom);
        foreach (var editor in Editors)
            ApplyEditorView(editor.Host);
        if (Settings.UiLanguage is "en" or "uz")
            Karvon.Claude.ClaudeLanguage.Instance.Language = Settings.UiLanguage;
    }
}
