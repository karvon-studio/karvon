using System.Text.Json;

namespace Karvon.Services;

/// <summary>Per-user settings persisted to ~/.config/karvon/settings.json.</summary>
public sealed class Settings
{
    public string? LastSolution { get; set; }
    public double EditorZoom { get; set; } = 1.0;

    /// <summary>Tool window id → dock side ("Left", "Right", "Bottom") for windows the user moved.</summary>
    public Dictionary<string, string> ToolLayout { get; set; } = [];

    /// <summary>Tool group sizes, their front tabs and the main window's size (per user, like Visual Studio's window layout).</summary>
    public WindowLayout Layout { get; set; } = new();

    public sealed class WindowLayout
    {
        public double? LeftWidth { get; set; }
        public double? RightWidth { get; set; }
        public double? BottomHeight { get; set; }

        /// <summary>A side group folded to its activity bar.</summary>
        public bool LeftCollapsed { get; set; }
        public bool RightCollapsed { get; set; }

        /// <summary>The bottom group minimized to its tabs, and on Auto Hide (shown over the documents only when asked for).</summary>
        public bool BottomCollapsed { get; set; }
        public bool BottomAutoHide { get; set; }

        /// <summary>Dock side ("Left", "Right", "Bottom") → the tool window in front.</summary>
        public Dictionary<string, string> SelectedTools { get; set; } = [];

        /// <summary>Dock side → its tool windows in the order they were dragged to (the default order otherwise).</summary>
        public Dictionary<string, List<string>> ToolOrder { get; set; } = [];

        /// <summary>Floating tool windows: tool window id → [x, y, width, height] of its window.</summary>
        public Dictionary<string, double[]> FloatingTools { get; set; } = [];

        /// <summary>The window's size when it is not maximized.</summary>
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; }
    }
    public List<string> RecentSolutions { get; set; } = [];

    /// <summary>When each recent solution was last opened (for the start window's Today / This week groups).</summary>
    public Dictionary<string, DateTime> RecentOpened { get; set; } = [];

    /// <summary>Solutions pinned to the top of the start window's recent list.</summary>
    public List<string> PinnedSolutions { get; set; } = [];

    /// <summary>Template identities used for new projects, most recent first.</summary>
    public List<string> RecentTemplates { get; set; } = [];

    public string? NewProjectLocation { get; set; }

    /// <summary>Claude Code panel: model alias ("default", "opus"...) and permission mode.</summary>
    public string? ClaudeModel { get; set; }

    public string? ClaudePermissionMode { get; set; }

    /// <summary>Claude Code reasoning effort chosen in the Modes menu (low ... max).</summary>
    public string? ClaudeEffort { get; set; }

    /// <summary>Remote Control was left on: each conversation's CLI turns it on as it starts.</summary>
    public bool ClaudeRemoteControl { get; set; }

    /// <summary>Claude Code gets Karvon's tools: the Database Explorer's data sources and the Docker engine (mcp__karvon__*).</summary>
    public bool ClaudeKarvonTools { get; set; } = true;

    /// <summary>
    /// Claude uses Karvon's tools for what they do: a Bash command they cover (dotnet build, docker, psql, ssh to a server,
    /// Jira's API) does not run, and Claude is told the tool instead.
    /// </summary>
    public bool ClaudeKarvonToolsFirst { get; set; } = true;

    /// <summary>
    /// Claude Code's own tools are kept away from the secrets on the machine: the files that hold them are not read or
    /// changed, and a shell command that reads one or asks a secret store does not run (Karvon's PreToolUse hook).
    /// </summary>
    public bool ClaudeKeepSecretsAway { get; set; } = true;

    /// <summary>
    /// Claude Code's shell runs in its own sandbox (bubblewrap on Linux, Seatbelt on macOS): what holds secrets not
    /// readable, no command out of it, the package registries reached without a prompt (<see cref="Karvon.Claude.ClaudeSandbox"/>).
    /// Off at first: some machines do not let it start, and on Linux it shuts out every Unix socket (ssh-agent's too).
    /// </summary>
    public bool ClaudeSandbox { get; set; }

    /// <summary>
    /// What Claude Code sends the model goes through a gateway inside Karvon (its ANTHROPIC_BASE_URL), which marks the
    /// secrets in it (Karvon.Claude's ClaudeGateway): beside the hook that marks what Claude Code's own tools give it,
    /// it covers what else a request holds (a file attached with @). On at first; a machine where the gateway cannot
    /// start goes straight to the API, and says so. (0.3.10's ClaudeGateway, off at first, is not read.)
    /// </summary>
    public bool ClaudeScanRequests { get; set; } = true;

    /// <summary>
    /// The user's rules for Karvon's tools: a tool's name ("db_query"), or its name and a target ("server_exec@prod-1",
    /// "db_query@shop", "k8s_scale@prod"), to "allow", "ask" or "deny"; enforced by Karvon's tool server whatever Claude
    /// Code's permission mode (<see cref="Karvon.Claude.ClaudeToolPolicy"/>).
    /// </summary>
    public Dictionary<string, string> ClaudeToolRules { get; set; } = [];

    /// <summary>
    /// The folders the user trusts, and what is in them: only there do Claude's sessions use what the repository brings, and
    /// build, test and run its code without asking (<see cref="Karvon.Claude.WorkspaceTrust"/>). This machine's, not synced.
    /// </summary>
    public List<string> TrustedFolders { get; set; } = [];

    /// <summary>What Claude does with Karvon's tools shows in Karvon: a query's results, a container's logs, the build, a search...</summary>
    public bool ClaudeShowToolActivity { get; set; } = true;

    /// <summary>Updates: checked each time Karvon Studio starts, downloaded at once, installed as it closes.</summary>
    public bool UpdatesCheck { get; set; } = true;

    public bool UpdatesDownload { get; set; } = true;

    public bool UpdatesInstallOnClose { get; set; } = true;

    /// <summary>When the updates were last checked for.</summary>
    public DateTimeOffset? UpdatesLastCheck { get; set; }

    /// <summary>The ids of the modules turned off in Tools > Get Tools and Features.</summary>
    public List<string> DisabledModules { get; set; } = [];

    /// <summary>The folders of the components removed in Tools > Get Tools and Features: they go when Karvon Studio starts again.</summary>
    public List<string> RemovedComponents { get; set; } = [];

    /// <summary>
    /// The folders where ESLint may check the files (true) or may not (false), as the user answered: it runs a folder's
    /// own ESLint, configuration and plugins, its code. Kept here, never in the folder.
    /// </summary>
    public Dictionary<string, bool> EslintFolders { get; set; } = [];

    /// <summary>Karvon's Docker tools see every container, not only the open solution's compose project.</summary>
    public bool ClaudeAllContainers { get; set; }

    /// <summary>Where the skills marketplace finds skills: github:owner/repo[/folder][@ref], a registry's URL, or a registry on disk.</summary>
    public List<string> SkillSources { get; set; } = ["github:anthropics/skills"];

    /// <summary>The skills marketplace shows Agensi's (agensi.io), read from its MCP server.</summary>
    public bool SkillsAgensi { get; set; } = true;

    /// <summary>The memory file the others (AGENTS.md, GEMINI.md) are generated from.</summary>
    public string SkillsMemorySource { get; set; } = "CLAUDE.md";

    /// <summary>The generated memory files are written again when their source changes (never those written by hand).</summary>
    public bool SkillsSyncMemoryFiles { get; set; } = true;

    /// <summary>The Claude Code panel's language: "en" or "uz" (English when not set).</summary>
    public string? UiLanguage { get; set; }

    /// <summary>Markdown files show their preview beside the editor, as in Visual Studio 2022's Markdown editor.</summary>
    public bool MarkdownPreview { get; set; } = true;

    /// <summary>The Markdown preview is below the editor instead of to its right.</summary>
    public bool MarkdownPreviewBelow { get; set; }

    /// <summary>
    /// Debug > Enable Hot Reload: a run whose language applies code changes while it runs does so (Ctrl+F5 of a .NET
    /// project runs under dotnet watch).
    /// </summary>
    public bool HotReload { get; set; } = true;

    /// <summary>The editor's inlay hints (parameter names, types), CodeLens, sticky scroll and minimap (View menu).</summary>
    public bool InlayHints { get; set; } = true;

    public bool CodeLens { get; set; } = true;

    public bool StickyScroll { get; set; } = true;

    public bool Minimap { get; set; }

    /// <summary>The caret line's commit after its text (Git > Show Line Blame).</summary>
    public bool LineBlame { get; set; } = true;

    /// <summary>Exception Settings: the exceptions each debugger (by its type) breaks on, as chosen.</summary>
    public Dictionary<string, List<string>> ExceptionBreakpoints { get; set; } = [];

    /// <summary>The exceptions each debugger said it can break on, last time it ran (for Exception Settings before it runs).</summary>
    public Dictionary<string, List<Debugging.ExceptionFilter>> ExceptionFilters { get; set; } = [];

    /// <summary>The Claude Code account (its configuration folder) the panel runs with; null for the CLI's own choice.</summary>
    public string? ClaudeAccount { get; set; }
    /// <summary>
    /// Per-solution state of older Karvon versions; it now lives in each solution's .vs folder (see
    /// <see cref="Services.SolutionState"/>), which moves an entry out of here the first time the solution opens.
    /// </summary>
    public Dictionary<string, SolutionState> Solutions { get; set; } = [];

    public sealed class SolutionState
    {
        public List<string> OpenDocuments { get; set; } = [];
        public string? ActiveDocument { get; set; }
        public string? StartupProject { get; set; }
        public string? LaunchProfile { get; set; }
        public string Configuration { get; set; } = "Debug";
        public Dictionary<string, List<int>> Breakpoints { get; set; } = [];
        public List<string> Watches { get; set; } = [];
    }

    /// <summary>Karvon's configuration folder (~/.config/karvon, or KARVON_CONFIG_DIR).</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("KARVON_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "karvon");

    static string FilePath => Path.Combine(ConfigDirectory, "settings.json");

    const int MaxRecent = 20;

    public void AddRecent(string solutionPath)
    {
        LastSolution = solutionPath;
        RecentSolutions.Remove(solutionPath);
        RecentSolutions.Insert(0, solutionPath);
        RecentOpened[solutionPath] = DateTime.Now;
        // Pinned entries never fall off the list.
        while (RecentSolutions.Count > MaxRecent && RecentSolutions.LastOrDefault(p => !PinnedSolutions.Contains(p)) is { } oldest)
            RemoveRecent(oldest);
    }

    public void RemoveRecent(string solutionPath)
    {
        RecentSolutions.Remove(solutionPath);
        RecentOpened.Remove(solutionPath);
        PinnedSolutions.Remove(solutionPath);
        if (LastSolution == solutionPath)
            LastSolution = RecentSolutions.FirstOrDefault();
    }

    public void AddRecentTemplate(string identity)
    {
        RecentTemplates.Remove(identity);
        RecentTemplates.Insert(0, identity);
        if (RecentTemplates.Count > 5)
            RecentTemplates.RemoveRange(5, RecentTemplates.Count - 5);
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt settings should never block startup.
        }
        return new Settings();
    }

    /// <summary>Settings Sync on this machine: null when it is off.</summary>
    public SettingsSyncState? Sync { get; set; }

    /// <summary>Saved (written, or not when in memory): Settings Sync sends what changed.</summary>
    public event Action? Saved;

    /// <summary>Kept in memory only: <see cref="Save"/> does not write it (a test's own settings, beside those the others share).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool InMemory { get; set; }

    public void Save()
    {
        Saved?.Invoke();
        if (InMemory)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }
}
