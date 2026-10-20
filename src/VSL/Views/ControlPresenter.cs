using Avalonia;
using Avalonia.Controls;

namespace VSL.Views;

/// <summary>
/// Shows a control that belongs to a view model (a document's editor, a diff view) and takes it from where it was shown
/// before: a document moved to another tab group or floating window brings its editor along, with its caret, scroll
/// position and undo history, instead of failing on a control that already has a parent.
/// </summary>
public sealed class ControlPresenter : Decorator
{
    public static readonly StyledProperty<Control?> ControlProperty = AvaloniaProperty.Register<ControlPresenter, Control?>(nameof(Control));

    public Control? Control
    {
        get => GetValue(ControlProperty);
        set => SetValue(ControlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ControlProperty)
            Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show();
    }

    void Show()
    {
        var control = Control;
        if (Child == control)
            return;
        switch (control?.Parent)
        {
            case Decorator previous:
                previous.Child = null;
                break;
            case ContentControl previous:
                previous.Content = null;
                break;
            case Panel previous:
                previous.Children.Remove(control);
                break;
        }
        Child = control;
    }
}
