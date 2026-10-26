namespace Karvon.Editor;

/// <summary>
/// A module's own Quick Action (Ctrl+.) for a problem in a file, beside its language server's: what it says, and what
/// it does (Python's "Create a virtual environment and install requirements.txt" for an import Pyright cannot resolve).
/// </summary>
public sealed record EditorQuickAction(string Title, Func<Task> RunAsync);
