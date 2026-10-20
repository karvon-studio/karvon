using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>
/// A floating document window (Window > Float, or a tab dragged out of the main window): one tab group of its own.
/// Closing it closes its documents, asking to save each; the main window's shortcuts work in it too.
/// </summary>
public partial class DocumentWindow : Window
{
    bool _closing;

    // For the XAML loader.
    public DocumentWindow()
    {
        InitializeComponent();
        Shell = null!;
        Group = null!;
    }

    public DocumentWindow(MainWindow shell, DocumentGroup group)
    {
        InitializeComponent();
        Shell = shell;
        Group = group;
        DataContext = shell.DataContext;
        GroupView.DataContext = group;
        group.PropertyChanged += OnGroupChanged;
        UpdateTitle();
        AddHandler(KeyDownEvent, (_, e) => shell.HandleKeyFromFloatingWindow(e), RoutingStrategies.Tunnel);
    }

    public MainWindow Shell { get; }

    public DocumentGroup Group { get; }

    internal DocumentGroupView View => GroupView;

    internal Canvas Drop => DropLayer;

    internal Border Preview => DropPreview;

    void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentGroup.Active))
            UpdateTitle();
    }

    void UpdateTitle() => Title = Group.Active is { } doc ? $"{doc.Title} - VSL" : "VSL";

    /// <summary>The group went away (its last tab moved or closed): the window closes without asking anything.</summary>
    internal void CloseForGroupRemoved()
    {
        _closing = true;
        Group.PropertyChanged -= OnGroupChanged;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The main window closing asks about unsaved documents itself (and closes this one when it closes).
        if (_closing || DataContext is not MainViewModel vm || e.CloseReason is WindowCloseReason.OwnerWindowClosing or WindowCloseReason.ApplicationShutdown)
            return;
        e.Cancel = true;
        // Closing the documents removes the group, which closes this window.
        await vm.CloseGroupAsync(Group);
    }
}
