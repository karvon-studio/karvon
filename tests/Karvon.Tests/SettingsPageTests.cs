using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Completions;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Settings (Ctrl+,): every setting of Karvon's and its modules' in one place, its search, and what it changes.</summary>
public class SettingsPageTests
{
    /// <summary>A view model whose settings are its own, in memory.</summary>
    static MainViewModel NewVm(Action<Karvon.Services.Settings>? configure = null) => NewViewModel(settings =>
    {
        settings.InMemory = true;
        configure?.Invoke(settings);
    });

    static T Find<T>(SettingsDocument settings, string title) where T : Setting =>
        settings.Pages.SelectMany(p => p.Groups).SelectMany(g => g.Settings).OfType<T>().Single(s => s.Title == title);

    [AvaloniaFact]
    public void Ctrl_comma_opens_one_settings_document_with_the_pages_of_karvon_and_its_modules()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 1000 };
        window.Show();
        // From the toolbar, the Tools menu and the key: the same document, in front.
        Assert.True(window.FindControl<Button>("SettingsButton")!.Command!.CanExecute(null));
        Assert.Contains(window.KeyBindings, k => k.Gesture.Key == Avalonia.Input.Key.OemComma && k.Command == vm.ShowSettingsCommand);
        window.FindControl<Button>("SettingsButton")!.Command!.Execute(null);
        var settings = Assert.IsType<SettingsDocument>(vm.ActiveDocument);
        vm.ShowSettingsCommand.Execute("claude");
        Assert.Same(settings, Assert.Single(vm.Documents.OfType<SettingsDocument>()));
        Assert.Equal("claude", settings.SelectedPage!.Id);
        Assert.Equal(["General", "Editor", "Run and Debug", "Claude and Agents", "Tool Windows", "Languages"], settings.Pages.Select(p => p.Title));
        // The modules' groups are in Karvon's pages, and their settings in its groups.
        Assert.Equal(["View", "Markdown", "Inline Completions"], settings.Pages.Single(p => p.Id == "editor").Groups.Select(g => g.Title));
        Assert.Equal(["Give Claude Karvon's tools", "Before the shell", "Show what Claude does in Karvon", "Docker: every container", "Tools and their rules"],
            settings.Pages.Single(p => p.Id == "claude").Groups.Single(g => g.Title == "Karvon's tools").Settings.Select(s => s.Title).Order().OrderBy(t => t switch
            {
                "Give Claude Karvon's tools" => 0, "Before the shell" => 1, "Show what Claude does in Karvon" => 2, "Docker: every container" => 3, _ => 4,
            }));
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "settings-claude");
        settings.ShowPage("editor");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "settings-editor");
        settings.ShowPage("general");
        Dispatcher.UIThread.RunJobs();
        // No heading over settings that do not apply (no vault here).
        Assert.Equal(vm.HasVault, settings.Sections.Single(s => s.Title == "Vault").IsShown);
        Snapshot(window, "settings-general");
        var menu = window.FindControl<MenuItem>("SettingsMenuItem")!;
        Assert.Equal("Ctrl+,", menu.InputGesture!.ToString("p", null).Replace("⌘", "Ctrl+"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_search_finds_settings_in_every_page_and_the_page_chosen_before_shows_again_after_it()
    {
        var vm = NewVm();
        vm.ShowSettingsCommand.Execute("editor");
        var settings = vm.Documents.OfType<SettingsDocument>().Single();
        settings.Search = "sand box";
        Assert.Null(settings.SelectedPage);
        Assert.Equal("Search results", settings.Heading);
        Assert.Contains(settings.Sections, s => s.Title == "Claude and Agents: Safety");
        // By its name in settings.json too, whatever its case; every word is to be there.
        settings.Search = "stickyscroll";
        Assert.Equal(["Sticky scroll"], settings.Sections.SelectMany(s => s.Settings).Select(s => s.Title));
        settings.Search = "zzz";
        Assert.True(settings.HasNoResults);
        // What does not apply now is not found (the sandbox's folders while it is off).
        settings.Search = "folders the agents may read";
        Assert.True(settings.HasNoResults);
        vm.Settings.StrictSandbox = true;
        settings.Search = "folders the agents may read ";
        Assert.False(settings.HasNoResults);
        settings.Search = "";
        Assert.Equal("editor", settings.SelectedPage!.Id);
        Assert.Equal("Editor", settings.Heading);
        // A page chosen ends the search.
        settings.Search = "update";
        settings.SelectedPage = settings.Pages.Single(p => p.Id == "general");
        Assert.Equal("", settings.Search);
        Assert.Equal("General", settings.Heading);
    }

    [AvaloniaFact]
    public async Task A_change_is_kept_at_once_and_what_is_changed_elsewhere_shows()
    {
        var vm = NewVm(s => s.TrustedFolders = ["/work/a", "/work/b"]);
        var saved = 0;
        vm.Settings.Saved += () => saved++;
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        vm.ShowSettingsCommand.Execute("editor");
        var settings = vm.Documents.OfType<SettingsDocument>().Single();

        // The editor's: as the View menu changes them.
        var minimap = Find<SwitchSetting>(settings, "Minimap");
        minimap.IsOn = true;
        Assert.True(vm.Minimap);
        Assert.Equal(1, saved);
        vm.ToggleMinimapCommand.Execute(null);
        Assert.False(minimap.IsOn);
        var zoom = Find<ChoiceSetting>(settings, "Zoom");
        zoom.Choice = zoom.Choices.Single(c => c.Value == "150");
        Assert.Equal(1.5, vm.Settings.EditorZoom);
        Assert.Equal(1.5, Karvon.Editor.EditorHost.DefaultZoom);
        // A zoom that is none of the choices is one as it is.
        vm.Settings.EditorZoom = 1.35;
        Assert.Equal("135", zoom.Choice!.Value);
        // The check box of the window follows a change made elsewhere.
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.DataContext is SwitchSetting { Title: "Inlay hints" });
        Assert.True(box.IsChecked);
        vm.ToggleInlayHintsCommand.Execute(null);
        await WaitUntil(() => box.IsChecked == false, TimeSpan.FromSeconds(5));
        Assert.False(box.IsChecked);

        // The preview below applies only with the preview.
        var below = Find<SwitchSetting>(settings, "Preview below the editor");
        Assert.True(below.IsShown);
        Find<SwitchSetting>(settings, "Markdown preview").IsOn = false;
        Assert.False(vm.MarkdownPreview);
        Assert.False(below.IsShown);

        // Claude's, and the strict sandbox's folders; those it does not give are said.
        Find<SwitchSetting>(settings, "Keep the secrets away").IsOn = false;
        Assert.False(vm.Settings.ClaudeKeepSecretsAway);
        Find<SwitchSetting>(settings, "Keep the agents to the project (strict sandbox)").IsOn = true;
        var read = Find<TextSetting>(settings, "Folders the agents may read besides the project");
        Assert.True(read.IsShown);
        read.Text = "/no/such/folder\n\n/no/such/folder\n";
        Assert.Equal(["/no/such/folder"], vm.Settings.SandboxReadFolders);
        Assert.Contains("/no/such/folder", read.ProblemText);
        var trusted = Find<EntriesSetting>(settings, "Trusted folders");
        Assert.Equal(["/work/a", "/work/b"], trusted.Entries.Select(e => e.Text));
        trusted.Entries[0].Remove.Execute(null);
        Assert.Equal(["/work/b"], vm.Settings.TrustedFolders);
        Assert.False(WorkspaceTrust.IsTrusted(vm.Settings.TrustedFolders, "/work/a"));
        Assert.Equal(["/work/b"], trusted.Entries.Select(e => e.Text));
        var language = Find<ChoiceSetting>(settings, "Language");
        language.Choice = language.Choices.Single(c => c.Value == "uz");
        Assert.Equal(("uz", "uz"), (vm.Settings.UiLanguage!, ClaudeLanguage.Instance.Language));
        ClaudeLanguage.Instance.Language = "en";

        // The Karvon page of MCP Servers shows the same settings, and follows.
        var mcp = vm.Module<Karvon.Mcp.McpModule>()!;
        var tools = Find<ActionSetting>(settings, "Tools and their rules");
        await tools.Actions[0].Command.ExecuteAsync(null);
        var page = Assert.IsType<Karvon.Mcp.ViewModels.McpBuiltInDocument>(vm.ActiveDocument);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Find<SwitchSetting>(settings, "Before the shell").IsOn = false;
        Assert.False(page.ToolsFirst);
        await WaitUntil(() => changed.Contains(nameof(page.ToolsFirst)), TimeSpan.FromSeconds(5));
        Assert.Contains(nameof(page.ToolsFirst), changed);

        // Closed, it listens no more.
        vm.ShowSettingsCommand.Execute(null);
        await vm.CloseDocumentCommand.ExecuteAsync(settings);
        Assert.Empty(vm.Documents.OfType<SettingsDocument>());
        window.Close();
    }

    [AvaloniaFact]
    public void Inline_completions_are_set_in_the_editor_page_and_kept_in_their_own_file()
    {
        var vm = NewVm();
        var completions = vm.Module<CompletionsModule>()!;
        vm.ShowSettingsCommand.Execute("editor");
        var settings = vm.Documents.OfType<SettingsDocument>().Single();
        var provider = Find<ChoiceSetting>(settings, "Suggestions come from");
        var url = Find<TextSetting>(settings, "Ollama's address");
        Assert.False(provider.IsShown);
        Find<SwitchSetting>(settings, "Suggest code as you type").IsOn = true;
        Assert.True(provider.IsShown);
        Assert.False(url.IsShown);
        provider.Choice = provider.Choices.Single(c => c.Value == "Ollama");
        Assert.True(url.IsShown);
        Assert.False(Find<ChoiceSetting>(settings, "Claude's model").IsShown);
        // An address that is none is said and not kept.
        url.Text = "localhost";
        Assert.NotNull(url.Refused);
        Assert.Equal(CompletionSettings.DefaultOllamaUrl, completions.Settings.OllamaUrl);
        url.Text = "http://box:11434";
        Assert.Null(url.Refused);
        var kept = CompletionSettings.Load(completions.SettingsFile);
        Assert.Equal((true, CompletionProvider.Ollama, "http://box:11434"), (kept.Enabled, kept.Provider, kept.OllamaUrl));
    }

    [AvaloniaFact]
    public void The_installer_and_settings_json_open_from_settings_and_eslint_folders_are_removed()
    {
        var vm = NewVm(s => s.EslintFolders = new() { ["/web"] = true, ["/old"] = false });
        var asked = new List<(InstallerPage, bool)>();
        vm.InstallerRequested = (page, check) => asked.Add((page, check));
        vm.ShowSettingsCommand.Execute(null);
        var settings = vm.Documents.OfType<SettingsDocument>().Single();
        Find<ActionSetting>(settings, "Tools and features").Actions[0].Command.Execute(null);
        Find<ActionSetting>(settings, "Karvon Studio Installer").Actions[0].Command.Execute(null);
        Assert.Equal([(InstallerPage.Modify, false), (InstallerPage.Product, true)], asked);
        var eslint = Find<EntriesSetting>(settings, "ESLint's folders");
        Assert.Equal(["/old: not checked", "/web: checked"], eslint.Entries.Select(e => e.Text));
        eslint.Entries[1].Remove.Execute(null);
        Assert.Equal(["/old"], vm.Settings.EslintFolders.Keys);
        // Settings Sync: what it does now, and the buttons that apply.
        var sync = Find<ActionSetting>(settings, "Settings Sync");
        Assert.Equal("Off", sync.State);
        Assert.Equal(["Turn On..."], sync.Actions.Where(a => a.IsShown).Select(a => a.Text));
        vm.Settings.Sync = new() { Target = "folder", Location = "/sync" };
        sync.Refresh();
        Assert.StartsWith("On: /sync", sync.State);
        Assert.Equal(["Sync Now", "Turn Off"], sync.Actions.Where(a => a.IsShown).Select(a => a.Text));
        sync.Actions.Single(a => a.Text == "Turn Off").Command.Execute(null);
        Assert.Null(vm.Settings.Sync);
        Assert.Equal("Off", sync.State);
    }

    [AvaloniaFact]
    public void A_module_that_is_off_has_no_settings_there()
    {
        var vm = NewVm(s => s.DisabledModules = ["claude", "agents", "completions", "typescript"]);
        vm.ShowSettingsCommand.Execute(null);
        var settings = vm.Documents.OfType<SettingsDocument>().Single();
        Assert.DoesNotContain("languages", settings.Pages.Select(p => p.Id));
        Assert.DoesNotContain("Inline Completions", settings.Pages.SelectMany(p => p.Groups).Select(g => g.Title));
        Assert.DoesNotContain("Safety", settings.Pages.SelectMany(p => p.Groups).Select(g => g.Title));
    }
}
