using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.Services;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What no code caught does not end Karvon Studio: it is logged, with its secrets hidden, and shown.</summary>
public class ErrorReportsTests
{
    [AvaloniaFact]
    public async Task What_nothing_caught_is_logged_shown_and_Karvon_goes_on()
    {
        var vm = NewViewModel();
        List<Exception> seen = [];
        void Show(Exception ex)
        {
            seen.Add(ex);
            vm.ShowError(ex);
        }
        ErrorReports.Reported += Show;
        ErrorReports.Install();
        try
        {
            var marker = "boom-" + Guid.NewGuid().ToString("N");
            var token = "ghp_" + new string('e', 36);
            // A job of the UI thread that throws.
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException($"{marker} with {token}"));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(seen);
            var log = File.ReadAllText(ErrorReports.LogFile);
            Assert.Contains(marker, log);
            Assert.DoesNotContain(token, log);
            Assert.Contains(marker, vm.OutputChannels.Single(c => c.Name == "Karvon").Document.Text);
            Assert.StartsWith("Something went wrong, and Karvon went on: " + marker, vm.StatusText);

            // An async handler's, as the export's was: awaited, then thrown on the UI thread.
            async void Click()
            {
                await Task.Yield();
                throw new IOException(marker + "-click");
            }
            Click();
            await WaitUntil(() => seen.Count == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(marker + "-click", seen[1].Message);
        }
        finally
        {
            ErrorReports.Uninstall();
            ErrorReports.Reported -= Show;
        }
    }

    [AvaloniaFact]
    public void The_status_bar_sends_the_user_to_Output_and_the_error_is_there()
    {
        var vm = NewViewModel();
        var window = new Karvon.Views.MainWindow { DataContext = vm };
        window.Show();
        vm.ShowToolWindow(Karvon.ViewModels.ToolWindows.Output);
        Dispatcher.UIThread.RunJobs();
        var shown = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "OutputEditor");
        Assert.Equal("", shown.Text);

        // Nothing to read in the channel it showed (Build, at the start): the window shows the error.
        var marker = "boom-" + Guid.NewGuid().ToString("N");
        vm.ShowError(new IOException(marker));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Karvon", vm.SelectedOutput.Name);
        Assert.Contains(marker, shown.Text);

        // A channel with text may be being read: it stays, and the error waits in its own.
        vm.SelectedOutput = vm.BuildOutput;
        vm.BuildOutput.Append("Build started");
        var later = marker + "-later";
        vm.ShowError(new IOException(later));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.BuildOutput, vm.SelectedOutput);
        Assert.Contains(later, vm.OutputChannels.Single(c => c.Name == "Karvon").Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_channel_asked_for_on_another_thread_keeps_its_text()
    {
        var vm = NewViewModel();
        Karvon.Modules.IWorkbench workbench = vm;
        // As a document tool's download does: the channel asked for and written to off the UI thread.
        var channel = await Task.Run(() =>
        {
            var made = workbench.Output("Off the UI thread");
            made.Append("first");
            return made;
        }, TestContext.Current.CancellationToken);
        Assert.Same(channel, workbench.Output("Off the UI thread"));
        Dispatcher.UIThread.RunJobs();
        channel.Append("second");
        Assert.Equal("first\nsecond\n", channel.Document.Text);
        Assert.Single(vm.OutputChannels, c => c.Name == "Off the UI thread");
    }
}

/// <summary>A query's result exported while the Database Explorer is not in a window (closed, or hidden).</summary>
public class ResultExportTests
{
    [AvaloniaFact]
    public async Task Exporting_a_result_with_the_explorer_closed_asks_in_the_results_window()
    {
        // The explorer shown once, then taken out of its window (its tool window closed): it is in no window.
        var store = new Bengine.Introspection.Servers.ServerStore(Path.Combine(Path.GetTempPath(), "karvon-export-" + Guid.NewGuid().ToString("N") + ".json"), null);
        var explorer = new Avalonia.Controls.Window { Content = new Karvon.Database.Views.DatabaseExplorerView(store) };
        explorer.Show();
        explorer.Content = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Null(Avalonia.Controls.TopLevel.GetTopLevel(Karvon.Database.Views.DatabaseExplorerView.Current));
        var result = new Bengine.Introspection.Model.QueryResult
        {
            IsQuery = true, Columns = ["id", "email"], ColumnTypes = ["int", "text"], Rows = [[1, "a@x.io"]], ElapsedMilliseconds = 1,
        };
        var view = new Karvon.Database.Views.ResultView { DataContext = new Karvon.Database.ViewModels.ResultSetViewModel("Result 1", "SELECT 1", result) };
        var window = new Avalonia.Controls.Window { Width = 800, Height = 400, Content = view };
        window.Show();
        var export = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Single(b => b.Content as string == "Export ▾");
        // A click's handler that throws once it has awaited: caught here, as Karvon catches it.
        List<Exception> errors = [];
        void Caught(Exception ex) => errors.Add(ex);
        ErrorReports.Reported += Caught;
        ErrorReports.Install();
        try
        {
            foreach (var item in ((Avalonia.Controls.MenuFlyout)export.Flyout!).Items.OfType<Avalonia.Controls.MenuItem>())
            {
                // Each export asks where in this window, whether the explorer is in one or not.
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
                await WaitUntil(() => errors.Count > 0, TimeSpan.FromMilliseconds(300));
            }
        }
        finally
        {
            ErrorReports.Uninstall();
            ErrorReports.Reported -= Caught;
        }
        Assert.Empty(errors);
        window.Close();
        explorer.Close();
    }
}
