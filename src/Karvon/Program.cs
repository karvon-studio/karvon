using Avalonia;

namespace Karvon;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The window's watchdog reading a dump of Karvon's threads: here, out of the process it reads (UiWatchdog).
        if (args is ["--read-stack", var dump, var thread] && int.TryParse(thread, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            foreach (var frame in Services.UiWatchdog.ReadStack(dump, id))
                Console.WriteLine(frame);
            return;
        }
        App.StartupArgs = args;
        // Child tools that look up `dotnet` on PATH (vstest.console, MSBuild nodes) must find the same SDK Karvon uses,
        // not a distro package that happens to be first on a desktop session's PATH.
        Build.DotnetCli.ApplyToCurrentProcess();
        DotNet.DevCertificates.ApplyToCurrentProcess();
        // The components removed in Tools > Get Tools and Features go before anything can run them.
        Modules.ModuleComponent.RemovePending(Services.Settings.Load(), Modules.ModuleComponent.DataRoot);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "Karvon" })
        .WithInterFont()
        .LogToTrace();
}
