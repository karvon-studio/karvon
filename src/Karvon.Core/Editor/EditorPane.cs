using Avalonia.Controls;

namespace Karvon.Editor;

/// <summary>
/// A module's pane beside the editor of a file it takes, as the Markdown preview is beside a Markdown file's (the HTTP
/// client's response beside a .http file): made once for the editor (see <c>KarvonModule.EditorPane</c>), disposed with it.
/// </summary>
public abstract class EditorPane : IDisposable
{
    /// <summary>The pane, to the right of the editor.</summary>
    public abstract Control View { get; }

    public virtual void Dispose()
    {
    }
}
