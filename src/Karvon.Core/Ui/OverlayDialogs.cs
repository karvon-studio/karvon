using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Ui;

/// <summary>A dialog over a module's tool window (its overlay); its <see cref="Result"/> is true when confirmed.</summary>
public abstract partial class OverlayDialog : ObservableObject
{
    readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected OverlayDialog(string title, string confirmText)
    {
        Title = title;
        ConfirmText = confirmText;
    }

    public string Title { get; }

    public string ConfirmText { get; }

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    public void Confirm()
    {
        if (CanConfirm)
            _result.TrySetResult(true);
    }

    [RelayCommand]
    public void Cancel() => _result.TrySetResult(false);

    public virtual bool CanConfirm => true;

    /// <summary>Whether it can be confirmed follows what is filled in: any change says it again (its button is bound to it).</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(CanConfirm))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanConfirm)));
    }
}

/// <summary>"Delete web-1?": what happens, and the button that does it (red when it cannot be undone).</summary>
public sealed class ConfirmDialog(string title, string message, string confirmText, bool isDanger) : OverlayDialog(title, confirmText)
{
    public string Message { get; } = message;

    public bool IsDanger { get; } = isDanger;
}

/// <summary>A name (a new volume, a new network), and a choice when there is one (the network's driver).</summary>
public sealed partial class InputDialog(string title, string label, string placeholder, string confirmText, IReadOnlyList<string>? choices = null)
    : OverlayDialog(title, confirmText)
{
    public string Label { get; } = label;

    public string Placeholder { get; } = placeholder;

    public IReadOnlyList<string> Choices { get; } = choices ?? [];

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Choice { get; set; } = choices?.FirstOrDefault();

    public override bool CanConfirm => Text.Trim().Length > 0;
}
