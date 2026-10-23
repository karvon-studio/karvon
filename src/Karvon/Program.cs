using Avalonia;

namespace Karvon;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartupArgs = args;
        // Child tools that look up `dotnet` on PATH (vstest.console, MSBuild nodes) must find the same SDK Karvon uses,
        // not a distro package that happens to be first on a desktop session's PATH.
        Build.DotnetCli.ApplyToCurrentProcess();
        DotNet.DevCertificates.ApplyToCurrentProcess();
        // The components removed in Tools > Modules go before anything can run them.
        Modules.ModuleComponent.RemovePending(Services.Settings.Load(), Modules.ModuleComponent.DataRoot);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "Karvon" })
        .WithInterFont()
        .LogToTrace();
}
