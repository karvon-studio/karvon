using Avalonia.Threading;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>An error nothing caught (<see cref="Karvon.Services.ErrorReports"/>): shown, and Karvon Studio goes on.</summary>
public partial class MainViewModel
{
    /// <summary>In Output (Karvon) whole, in the status bar by its message. The Output window turns to it unless it shows a channel with text, which may be being read.</summary>
    public void ShowError(Exception ex)
    {
        void Show()
        {
            var channel = ((IWorkbench)this).Output("Karvon");
            channel.Append($"{DateTime.Now:HH:mm:ss}  {Karvon.Claude.Tools.Redaction.Text(ex.ToString())}");
            if (SelectedOutput.Document.TextLength == 0)
                SelectedOutput = channel;
            SetStatus($"Something went wrong, and Karvon went on: {Karvon.Claude.Tools.Redaction.Text(ex.Message)} (Output: Karvon)");
        }
        if (Dispatcher.UIThread.CheckAccess())
            Show();
        else
            Dispatcher.UIThread.Post(Show);
    }
}
