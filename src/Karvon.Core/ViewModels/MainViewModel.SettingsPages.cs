using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;
using Karvon.Editor;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>Settings (Tools > Settings, Ctrl+,): the document, and the core's pages (the modules add theirs).</summary>
public partial class MainViewModel
{
    /// <summary>Settings, opened (or put in front), on a page when its id is given.</summary>
    [RelayCommand]
    void ShowSettings(string? page)
    {
        var settings = Documents.OfType<SettingsDocument>().FirstOrDefault();
        if (settings is null)
            Documents.Add(settings = new SettingsDocument(Settings, [.. SettingsPages(), .. Modules.SelectMany(m => m.SettingsPages())]));
        settings.ShowPage(page);
        ActiveDocument = settings;
    }

    void IWorkbench.ShowSettings(string? page) => ShowSettings(page);

    /// <summary>Karvon Studio Installer, as the window shows it (set by the window): its product (checking for updates) or Modify.</summary>
    public Action<InstallerPage, bool>? InstallerRequested { get; set; }

    /// <summary>A switch kept in settings.json.</summary>
    SwitchSetting Switch(string title, string? description, string name, Func<bool> get, Action<bool> set) =>
        new(title, description, get, on =>
        {
            set(on);
            Settings.Save();
        }) { Keywords = name };

    IEnumerable<SettingsPage> SettingsPages()
    {
        yield return new SettingsPage("general", "General", "Settings",
        [
            new("Updates",
            [
                Switch("Check for updates when Karvon Studio starts", null, nameof(Settings.UpdatesCheck), () => Settings.UpdatesCheck, on => Settings.UpdatesCheck = on),
                Switch("Download an update at once", "Off: it is downloaded when you ask for it.", nameof(Settings.UpdatesDownload),
                    () => Settings.UpdatesDownload, on => Settings.UpdatesDownload = on),
                Switch("Install an update as Karvon Studio closes", "Off: it is installed when you restart to update.", nameof(Settings.UpdatesInstallOnClose),
                    () => Settings.UpdatesInstallOnClose, on => Settings.UpdatesInstallOnClose = on),
                new ActionSetting("Karvon Studio Installer", "The installed version and its update.", null, Do("Check for Updates", () => InstallerRequested?.Invoke(InstallerPage.Product, true))),
            ]),
            new("Modules",
            [
                new ActionSetting("Tools and features", "Languages, tool windows and Claude Code, turned on and off, and what they download.", null,
                    Do("Get Tools and Features...", () => InstallerRequested?.Invoke(InstallerPage.Modify, false))),
            ]),
            new("Settings Sync",
            [
                new ActionSetting("Settings Sync", "Your preferences on all your machines, through a folder that syncs or a secret GitHub gist.", SettingsSyncState,
                    new SettingAction("Turn On...", () => TurnOnSettingsSyncCommand.ExecuteAsync(null)) { ShownWhen = () => !IsSettingsSyncOn },
                    new SettingAction("Sync Now", () => SyncSettingsNowCommand.ExecuteAsync(null)) { ShownWhen = () => IsSettingsSyncOn },
                    new SettingAction("Turn Off", () => Done(() => TurnOffSettingsSyncCommand.Execute(null))) { ShownWhen = () => IsSettingsSyncOn }),
            ]),
            new("Vault",
            [
                new ActionSetting("Karvon's vault", "Where Karvon keeps passwords and keys, encrypted.", () =>
                        IsVaultLocked ? "Locked" : HasVaultPassphrase ? "Opened by its passphrase, asked as Karvon Studio starts" : "Opened by the system keyring as you sign in to the computer",
                    new SettingAction("Set Passphrase...", () => SetVaultPassphraseCommand.ExecuteAsync(null)) { ShownWhen = () => CanSetVaultPassphrase },
                    new SettingAction("Change Passphrase...", () => SetVaultPassphraseCommand.ExecuteAsync(null)) { ShownWhen = () => HasVaultPassphrase },
                    new SettingAction("Remove Passphrase...", () => RemoveVaultPassphraseCommand.ExecuteAsync(null)) { ShownWhen = () => HasVaultPassphrase },
                    new SettingAction("Unlock...", () => UnlockVaultCommand.ExecuteAsync(null)) { ShownWhen = () => IsVaultLocked })
                {
                    ShownWhen = () => HasVault,
                },
            ]),
            new("Files",
            [
                new ActionSetting("settings.json", "Where Karvon keeps these settings. What is changed in the file itself is read when Karvon Studio starts.", () => Settings.SettingsFile,
                    Do("Open settings.json", () => OpenDocument(Settings.SettingsFile))),
            ]),
        ]);
        yield return new SettingsPage("editor", "Editor", "Pencil",
        [
            new("View",
            [
                new ChoiceSetting("Zoom", "The editors' text (Ctrl+wheel, Ctrl+Shift+. and Ctrl+Shift+,).",
                    [.. new[] { 70, 80, 90, 100, 110, 125, 150, 175, 200 }.Select(z => new SettingChoice($"{z}", $"{z} %"))],
                    () => $"{Math.Round(Settings.EditorZoom * 100)}", SetEditorZoom) { Keywords = nameof(Settings.EditorZoom) + " font size" },
                new SwitchSetting("Inlay hints", "Parameter names and types in gray in the code.", () => InlayHints, on => InlayHints = on) { Keywords = nameof(InlayHints) },
                new SwitchSetting("CodeLens", "A declaration's references above it.", () => CodeLens, on => CodeLens = on),
                new SwitchSetting("Sticky scroll", "The declarations the first line is in, kept at the top of the editor.", () => StickyScroll, on => StickyScroll = on) { Keywords = nameof(StickyScroll) },
                new SwitchSetting("Minimap", "The whole file, small, beside the scroll bar.", () => Minimap, on => Minimap = on),
                new SwitchSetting("Line blame", "The caret line's commit after its text.", () => LineBlame, on => LineBlame = on) { Keywords = nameof(LineBlame) + " git" },
            ]),
            new("Markdown",
            [
                new SwitchSetting("Markdown preview", "A Markdown file's preview beside its editor.", () => MarkdownPreview, _ => ToggleMarkdownPreview()) { Keywords = nameof(MarkdownPreview) },
                new SwitchSetting("Preview below the editor", "Instead of to its right.", () => MarkdownPreviewBelow, _ => ToggleMarkdownPreviewBelow())
                {
                    Keywords = nameof(MarkdownPreviewBelow),
                    ShownWhen = () => MarkdownPreview,
                },
            ]),
        ]);
        yield return new SettingsPage("debugging", "Run and Debug", "Start",
        [
            new("Hot Reload",
            [
                new SwitchSetting("Hot Reload", "What runs applies your changes as they are saved; a program whose language cannot is restarted with them.", () => HotReload, on => HotReload = on)
                {
                    Keywords = nameof(HotReload) + " restart on save",
                },
            ]),
            new("Exceptions",
            [
                new ActionSetting("Exception Settings", "The exceptions each debugger breaks on.", null, Do("Exception Settings", () => ShowExceptionSettingsCommand.Execute(null))),
            ]),
        ]);
        if (Modules.Any(m => m.Id is "claude" or "agents"))
            yield return ClaudePage();
    }

    SettingsPage ClaudePage() => new("claude", "Claude and Agents", "Claude",
    [
        new("Safety",
        [
            Switch("Keep the secrets away", "Claude Code's own tools do not read or change a file that holds secrets (.env, keys, certificates, credentials, the SSH keys, the "
                + "kubeconfig, Karvon's vault), and a shell command that reads one or asks a secret store does not run. What its tools give the model has its secrets marked. "
                + "For the sessions started after.", nameof(Settings.ClaudeKeepSecretsAway), () => Settings.ClaudeKeepSecretsAway, on => Settings.ClaudeKeepSecretsAway = on),
            Switch("Scan what Claude Code sends the model", "Its requests go through a gateway inside Karvon, on this machine only, which marks the secrets in them, a file attached "
                + "with @ among them. Not with Bedrock, Vertex or Foundry. For the sessions started after.", nameof(Settings.ClaudeScanRequests),
                () => Settings.ClaudeScanRequests, on => Settings.ClaudeScanRequests = on),
            new SwitchSetting("Run Claude's shell in a sandbox", "Claude Code's own sandbox (bubblewrap on Linux, Seatbelt on macOS): what holds secrets cannot be read, no command runs "
                + "outside it. On Linux no Unix socket is reached from it: not the keyring, not ssh-agent. For the sessions started after.", () => Settings.ClaudeSandbox, on =>
            {
                Settings.ClaudeSandbox = on;
                Settings.Save();
            })
            {
                Keywords = nameof(Settings.ClaudeSandbox),
                Problem = () => Settings.ClaudeSandbox ? ClaudeSandbox.Problem() : null,
            },
            new SwitchSetting("Keep the agents to the project (strict sandbox)", "Their shell and file tools see the project (and change only it), the toolchains, the package caches and "
                + "git's settings, and nothing else of yours. A command that needs more asks you every time. Not for Codex and other agents that run their own commands. "
                + "For the sessions started after.", () => Settings.StrictSandbox, on =>
            {
                Settings.StrictSandbox = on;
                Settings.Save();
            })
            {
                Keywords = nameof(Settings.StrictSandbox),
                Problem = () => Settings.StrictSandbox ? ClaudeSandbox.Problem() : null,
            },
            Folders("Folders the agents may read besides the project", "~/Documents/reports", nameof(Settings.SandboxReadFolders),
                () => Settings.SandboxReadFolders, folders => Settings.SandboxReadFolders = folders, folders => Sandbox(folders, [])),
            Folders("Folders the agents may change besides the project", "~/Documents/out", nameof(Settings.SandboxWriteFolders),
                () => Settings.SandboxWriteFolders, folders => Settings.SandboxWriteFolders = folders, folders => Sandbox([], folders)),
            new EntriesSetting("Trusted folders", "Only in them (and what is in them) do Claude's sessions use what a repository brings, and build, test and run its code without asking. "
                + "A folder is trusted in the Claude Code window.", () => Settings.TrustedFolders.Select(f => (f, f)), folder =>
            {
                Settings.TrustedFolders.Remove(folder);
                Settings.Save();
            })
            {
                Keywords = nameof(Settings.TrustedFolders),
                Empty = "No folder is trusted.",
            },
        ]),
        new("Karvon's tools",
        [
            Switch("Give Claude Karvon's tools", "The build, the tests, the debugger, the code, the databases, Docker and the rest (mcp__karvon__*). For the sessions started after.",
                nameof(Settings.ClaudeKarvonTools), () => Settings.ClaudeKarvonTools, on => Settings.ClaudeKarvonTools = on),
            Switch("Before the shell", "Claude is told to use them, and a shell command one of them does (dotnet build, psql, docker ps, ssh to one of the Servers) is sent to it; the same "
                + "command again goes through. For the sessions started after.", nameof(Settings.ClaudeKarvonToolsFirst), () => Settings.ClaudeKarvonToolsFirst, on => Settings.ClaudeKarvonToolsFirst = on),
            Switch("Show what Claude does in Karvon", "A query's results, a container's logs, the build, a search, where you would have done it.", nameof(Settings.ClaudeShowToolActivity),
                () => Settings.ClaudeShowToolActivity, on => Settings.ClaudeShowToolActivity = on),
        ]),
        new("Window",
        [
            new ChoiceSetting("Language", "The Claude Code window's language.", [.. ClaudeLanguage.Languages.Select(l => new SettingChoice(l.Code, l.Label))], () => ClaudeLanguage.Instance.Language, language =>
            {
                ClaudeLanguage.Instance.Language = language;
                Settings.UiLanguage = language;
                Settings.Save();
            }) { Keywords = nameof(Settings.UiLanguage) },
        ]),
    ]);

    /// <summary>Folders the strict sandbox lets the agents into, one a line, while it is on; those it does not are said.</summary>
    TextSetting Folders(string title, string placeholder, string name, Func<List<string>> get, Action<List<string>> set, Func<List<string>, ProjectSandbox> sandbox) =>
        new(title, "One a line.", () => string.Join('\n', get()), text =>
        {
            set(TextSetting.Lines(text));
            Settings.Save();
        })
        {
            IsList = true,
            Placeholder = placeholder,
            Keywords = name,
            ShownWhen = () => Settings.StrictSandbox,
            Problem = () => Settings.StrictSandbox && get() is { Count: > 0 } folders && sandbox(folders).Refused is { Count: > 0 } refused
                ? "Not given (the home folder, or a place that holds secrets, or a folder that is not there): " + string.Join(", ", refused)
                : null,
        };

    ProjectSandbox Sandbox(List<string> read, List<string> write) =>
        new(((IWorkbench)this).Workspace ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), read, write);

    string? SettingsSyncState() => Settings.Sync is not { } sync ? "Off"
        : (SettingsSyncTargets.FirstOrDefault(t => t.Id == sync.Target) is { } target ? $"On: {(sync.Target == "folder" ? sync.Location : target.Name)}" : $"On: {sync.Target}, whose module is off")
          + (sync.LastSynced is { } last ? $", synced {last.LocalDateTime:g}" : "");

    void SetEditorZoom(string percent)
    {
        var zoom = Math.Clamp(int.Parse(percent) / 100.0, EditorHost.MinZoom, EditorHost.MaxZoom);
        EditorHost.DefaultZoom = Settings.EditorZoom = zoom;
        foreach (var editor in Editors)
            editor.Host.SetZoom(zoom);
        Settings.Save();
    }

    /// <summary>A button that does something at once.</summary>
    static SettingAction Do(string text, Action run) => new(text, () => Done(run));

    static Task Done(Action run)
    {
        run();
        return Task.CompletedTask;
    }
}
