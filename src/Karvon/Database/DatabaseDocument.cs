using System.ComponentModel;
using Karvon.ViewModels;

namespace Karvon.Database;

/// <summary>
/// A Database Explorer document (a console, a table's data, DDL, a diagram, a comparison...) in Karvon's document well.
/// The explorer keeps its documents; this tab follows one of them, and closing the tab closes it there.
/// </summary>
public sealed class DatabaseDocument : DocumentTab
{
    public DatabaseDocument(Database.ViewModels.DocumentViewModel inner)
    {
        Inner = inner;
        inner.PropertyChanged += OnInnerChanged;
    }

    public Database.ViewModels.DocumentViewModel Inner { get; }

    public override string Key { get; } = "db:" + Guid.NewGuid().ToString("N");

    public override string Title => Inner.Title;

    public override string ToolTip => Inner.ToolTip ?? Inner.Title;

    void OnInnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Title) or null or "")
            OnPropertyChanged(nameof(Title));
        if (e.PropertyName is nameof(ToolTip) or nameof(Title) or null or "")
            OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>The explorer closes its document (it may ask first); the tab goes when it does.</summary>
    public override bool CloseByOwner()
    {
        if (Inner.CanClose)
            Inner.Close?.Invoke();
        return true;
    }

    public override void Dispose() => Inner.PropertyChanged -= OnInnerChanged;
}
