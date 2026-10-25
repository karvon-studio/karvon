using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Services;

/// <summary>The synced settings as a store keeps them: their JSON, when and by which machine they were written.</summary>
public sealed record SyncedSettings(JsonObject Settings, DateTimeOffset Written, string Machine)
{
    public string ToJson() => new JsonObject
    {
        ["written"] = Written.ToString("O"),
        ["machine"] = Machine,
        ["settings"] = Settings.DeepClone(),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static SyncedSettings? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject root && root["settings"] is JsonObject settings
                ? new SyncedSettings((JsonObject)settings.DeepClone(), DateTimeOffset.TryParse((string?)root["written"], out var at) ? at : DateTimeOffset.MinValue, (string?)root["machine"] ?? "")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where Settings Sync keeps the settings: a folder (Dropbox's, Syncthing's, a share's), a secret GitHub gist (the Pipelines module's).</summary>
public interface ISettingsSyncStore
{
    /// <summary>What it is, in the status bar: "GitHub (a secret gist)", "~/Dropbox/karvon".</summary>
    string Name { get; }

    /// <summary>What is there; null when nothing is yet.</summary>
    Task<SyncedSettings?> ReadAsync(CancellationToken ct);

    Task WriteAsync(SyncedSettings settings, CancellationToken ct);
}

/// <summary>A kind of store Settings Sync offers: a folder (Karvon's own), GitHub (a module's).</summary>
public sealed record SettingsSyncTarget(string Id, string Name, string Description, Func<SettingsSyncState, ISettingsSyncStore> Open);

/// <summary>Settings Sync on this machine (not synced itself): where to, and what was synced last (the base a merge starts from).</summary>
public sealed class SettingsSyncState
{
    public string Target { get; set; } = "folder";

    /// <summary>The folder, or the gist's id once it is known.</summary>
    public string? Location { get; set; }

    public string? Last { get; set; }

    public DateTimeOffset? LastSynced { get; set; }
}

/// <summary>A folder's karvon-settings.json.</summary>
public sealed class FolderSettingsStore(string folder) : ISettingsSyncStore
{
    public const string FileName = "karvon-settings.json";

    public string Name => folder;

    string File_ => Path.Combine(folder, FileName);

    public async Task<SyncedSettings?> ReadAsync(CancellationToken ct) =>
        File.Exists(File_) ? SyncedSettings.Parse(await File.ReadAllTextAsync(File_, ct)) : null;

    public async Task WriteAsync(SyncedSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        // Whole or not at all: a sync tool never copies half of it.
        var temporary = File_ + ".karvon-part";
        await File.WriteAllTextAsync(temporary, settings.ToJson(), ct);
        File.Move(temporary, File_, overwrite: true);
    }
}

/// <summary>
/// The settings that go with the user from machine to machine, as VS Code's Settings Sync takes them: the preferences
/// (the editor's, Claude's, the updates', the modules on and off, the exceptions to break on...), not this machine's
/// paths, window, history, installs or account. Two machines' changes merge: what changed here since the last sync
/// stays here's, the rest is the other's.
/// </summary>
public static class SettingsSync
{
    public static readonly IReadOnlyList<string> Keys =
    [
        nameof(Settings.EditorZoom), nameof(Settings.ClaudeModel), nameof(Settings.ClaudePermissionMode), nameof(Settings.ClaudeEffort),
        nameof(Settings.ClaudeRemoteControl), nameof(Settings.ClaudeKarvonTools), nameof(Settings.ClaudeKarvonToolsFirst), nameof(Settings.ClaudeShowToolActivity),
        nameof(Settings.ClaudeAllContainers), nameof(Settings.UpdatesCheck), nameof(Settings.UpdatesDownload), nameof(Settings.UpdatesInstallOnClose),
        nameof(Settings.DisabledModules), nameof(Settings.SkillSources), nameof(Settings.SkillsAgensi), nameof(Settings.SkillsMemorySource),
        nameof(Settings.SkillsSyncMemoryFiles), nameof(Settings.UiLanguage), nameof(Settings.MarkdownPreview), nameof(Settings.MarkdownPreviewBelow),
        nameof(Settings.InlayHints), nameof(Settings.CodeLens), nameof(Settings.StickyScroll), nameof(Settings.Minimap), nameof(Settings.LineBlame),
        nameof(Settings.ExceptionBreakpoints),
    ];

    /// <summary>The settings synced, as JSON.</summary>
    public static JsonObject Of(Settings settings)
    {
        var all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        var synced = new JsonObject();
        foreach (var key in Keys)
            synced[key] = all[key]?.DeepClone();
        return synced;
    }

    /// <summary>Synced settings put in (those it has; another version's unknown ones are left out).</summary>
    public static void Apply(Settings settings, JsonObject synced)
    {
        var all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        foreach (var key in Keys)
            if (synced.ContainsKey(key))
                all[key] = synced[key]?.DeepClone();
        var merged = all.Deserialize<Settings>()!;
        foreach (var key in Keys)
            if (typeof(Settings).GetProperty(key) is { } property)
                property.SetValue(settings, property.GetValue(merged));
    }

    /// <summary>A three-way merge by setting: one changed here since <paramref name="last"/> is mine, any other theirs.</summary>
    public static JsonObject Merge(JsonObject? last, JsonObject mine, JsonObject theirs)
    {
        var merged = new JsonObject();
        foreach (var key in Keys)
        {
            var changedHere = last is null ? false : !JsonNode.DeepEquals(last[key], mine[key]);
            var value = changedHere || !theirs.ContainsKey(key) ? mine[key] : theirs[key];
            merged[key] = value?.DeepClone();
        }
        return merged;
    }

    public static bool Same(JsonObject a, JsonObject b) => JsonNode.DeepEquals(a, b);
}
