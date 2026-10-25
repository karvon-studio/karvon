namespace Karvon.ViewModels;

/// <summary>View > Inlay Hints, CodeLens, Sticky Scroll and Minimap: in every editor, kept in the settings.</summary>
public partial class MainViewModel
{
    public bool InlayHints
    {
        get => Settings.InlayHints;
        set => SetEditorView(value, Settings.InlayHints, v => Settings.InlayHints = v, (host, v) => host.ShowInlayHints = v);
    }

    public bool CodeLens
    {
        get => Settings.CodeLens;
        set => SetEditorView(value, Settings.CodeLens, v => Settings.CodeLens = v, (host, v) => host.ShowCodeLens = v);
    }

    public bool StickyScroll
    {
        get => Settings.StickyScroll;
        set => SetEditorView(value, Settings.StickyScroll, v => Settings.StickyScroll = v, (host, v) => host.ShowStickyScroll = v);
    }

    public bool Minimap
    {
        get => Settings.Minimap;
        set => SetEditorView(value, Settings.Minimap, v => Settings.Minimap = v, (host, v) => host.ShowMinimap = v);
    }

    void SetEditorView(bool value, bool current, Action<bool> keep, Action<Editor.EditorHost, bool> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (value == current)
            return;
        keep(value);
        Settings.Save();
        foreach (var editor in Editors)
            apply(editor.Host, value);
        OnPropertyChanged(name);
    }

    /// <summary>A new editor as the View menu has them.</summary>
    void ApplyEditorView(Editor.EditorHost host)
    {
        host.ShowInlayHints = Settings.InlayHints;
        host.ShowCodeLens = Settings.CodeLens;
        host.ShowStickyScroll = Settings.StickyScroll;
        host.ShowMinimap = Settings.Minimap;
    }
}
