using Karvon.Claude;
using Karvon.Services;
using Karvon.ViewModels;

namespace Karvon.Modules;

/// <summary>
/// What a module may use of Karvon Studio: the settings, the open folder, the document well, the tool windows, the
/// status bar, and what other parts of Karvon offer (<see cref="Service{T}"/>). Called on the UI thread.
/// </summary>
public interface IWorkbench
{
    Settings Settings { get; }

    void SaveSettings();

    /// <summary>The open solution's folder, or null.</summary>
    string? Workspace { get; }

    /// <summary>A solution was opened or closed.</summary>
    event Action? WorkspaceChanged;

    IEnumerable<DocumentTab> Documents { get; }

    /// <summary>A document in the document well (added when it is not there yet), in front.</summary>
    void ShowDocument(DocumentTab document);

    /// <summary>Closes a document, asking to save it when it has unsaved changes.</summary>
    void CloseDocument(DocumentTab document);

    /// <summary>A file in the editor, at a line when one is given.</summary>
    void OpenFile(string path, int line = 0);

    void ShowToolWindow(string id);

    /// <summary>A page in the desktop's browser.</summary>
    void OpenUrl(string url);

    /// <summary>A line in the status bar.</summary>
    void Status(string text);

    /// <summary>What Karvon or another module offers (<see cref="IClaudeService"/>), or null when it is off.</summary>
    T? Service<T>() where T : class;
}

/// <summary>Claude Code in Karvon, for the modules that work with it.</summary>
public interface IClaudeService
{
    /// <summary>The chosen account's configuration folder: ~/.claude, or its CLAUDE_CONFIG_DIR.</summary>
    string ConfigDirectory { get; }

    /// <summary>The chosen account's settings file; null for ~/.claude.json.</summary>
    string? SettingsFile { get; }

    /// <summary>The tools Karvon gives the Claude Code sessions it starts; empty when they are off.</summary>
    IReadOnlyList<IClaudeTool> BuiltInTools { get; }

    /// <summary>Another account was chosen, or Claude Code's settings changed: what was read from them is to be read again.</summary>
    event Action? Changed;

    /// <summary>The prompt in the Claude Code window, for the user to send.</summary>
    void Ask(string prompt);

    /// <summary>Claude Code signs in to one of its MCP servers; what it said.</summary>
    Task<string> AuthenticateMcpAsync(string name);

    /// <summary><c>claude mcp add</c>: why it failed, or null.</summary>
    Task<string?> AddMcpServerAsync(string name, string transport, string target, string scope);

    /// <summary>The MCP servers a session started in a folder gets beside its own: a file for <c>--mcp-config</c>, or null.</summary>
    Func<string, string?>? ExtraMcpServers { get; set; }
}
