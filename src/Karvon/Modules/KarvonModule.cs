using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Karvon.Modules;

/// <summary>Where a tool window is docked: a side group (an activity bar) or the bottom tabs.</summary>
public enum DockSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// A tool window a module adds: an icon on an activity bar and an item in the View menu. Its view is made the first time
/// it shows, so a module never opened does nothing.
/// </summary>
/// <param name="Id">Its id in the layout Karvon remembers ("Mcp").</param>
/// <param name="MenuHeader">Its View menu item, with its access key ("_MCP Servers").</param>
/// <param name="Icon">A <c>VsIcon</c> kind.</param>
public sealed record ToolWindowInfo(string Id, string Title, string MenuHeader, string Icon, DockSide Side, Func<Control> CreateView);

/// <summary>
/// A part of Karvon Studio that can be turned off in Tools > Modules (and, later, installed on its own). Karvon knows a
/// module only through this: what it adds to the window (tool windows; how its documents and dialogs look) and what it
/// does when Karvon starts and closes. A module uses Karvon through the <see cref="IWorkbench"/> it is started with.
/// </summary>
public abstract partial class KarvonModule : ObservableObject, IDisposable
{
    /// <summary>For the settings ("mcp"): which modules are off.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>What it does, for Tools > Modules.</summary>
    public abstract string Description { get; }

    public virtual IReadOnlyList<ToolWindowInfo> ToolWindows => [];

    /// <summary>How its documents look in the document well: a template for each of its document types.</summary>
    public virtual IReadOnlyList<IDataTemplate> DocumentTemplates => [];

    /// <summary>What it shows over the whole window (its dialogs and messages), once it has something to show.</summary>
    [ObservableProperty]
    public partial object? Overlay { get; protected set; }

    /// <summary>How <see cref="Overlay"/> looks.</summary>
    public virtual IDataTemplate? OverlayTemplate => null;

    /// <summary>Karvon, from <see cref="Start"/> on.</summary>
    protected IWorkbench Workbench { get; private set; } = null!;

    public bool IsStarted { get; private set; }

    /// <summary>Karvon has started with the module on (on the UI thread).</summary>
    public void Start(IWorkbench workbench)
    {
        Workbench = workbench;
        IsStarted = true;
        Started();
    }

    protected virtual void Started()
    {
    }

    /// <summary>Karvon closes: what the module started stops.</summary>
    public virtual void Dispose()
    {
    }

    /// <summary>A view of <typeparamref name="T"/>, and of nothing else (FuncDataTemplate&lt;T&gt; takes null for any T: a template of the application's would show in every empty presenter).</summary>
    protected static IDataTemplate Template<T>(Func<T, Control> view) where T : class =>
        new FuncDataTemplate(data => data is T, (data, _) => view((T)data!));

    /// <summary>A document of the module as Karvon's documents look: under the document well's blue line.</summary>
    protected static IDataTemplate Document<T>(Func<T, Control> view) where T : class => Template<T>(document => new Border
    {
        Classes = { "docframe" },
        BorderBrush = new SolidColorBrush(Color.Parse("#007ACC")),
        BorderThickness = new Thickness(0, 2, 0, 0),
        Child = view(document),
    });
}
