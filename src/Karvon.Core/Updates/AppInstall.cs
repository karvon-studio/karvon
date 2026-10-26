using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Karvon.Updates;

/// <summary>How a Karvon Studio was installed, which says how it updates.</summary>
public enum Distribution
{
    /// <summary>Built from its sources (scripts/install.sh, dotnet run): it updates with git pull.</summary>
    Source,
    /// <summary>Debian's package, in /opt/karvon: apt installs the next one (as root, which pkexec asks for).</summary>
    Deb,
    /// <summary>The AppImage: its file is replaced.</summary>
    AppImage,
    /// <summary>The Linux .tar.gz, unpacked anywhere: its folder is replaced.</summary>
    Tar,
    /// <summary>Karvon Studio.app, from the .dmg (or Homebrew's cask): the bundle is replaced.</summary>
    Dmg,
    /// <summary>Windows' installer (Inno Setup): the next one runs, silently.</summary>
    Setup,
    /// <summary>Windows' .zip, unpacked anywhere: its files are copied over.</summary>
    Zip,
}

/// <summary>
/// This Karvon Studio: its version, how it was installed (the package writes a karvon-distribution file beside the
/// program; a build of the sources has none), and where it is.
/// </summary>
/// <param name="Directory">The program's folder.</param>
/// <param name="AppImage">The AppImage's own file, when it runs from one.</param>
public sealed record AppInstall(Distribution Distribution, string Version, string Directory, string? AppImage = null)
{
    /// <summary>The file a package writes beside the program, naming the package: deb, appimage, tar, dmg, setup, zip.</summary>
    public const string MarkerFile = "karvon-distribution";

    /// <summary>The Karvon Studio that runs.</summary>
    public static AppInstall Current { get; } = Of(AppContext.BaseDirectory, CurrentVersion, Environment.GetEnvironmentVariable("APPIMAGE"));

    /// <summary>The version this build has (the release's tag, without the commit it was built from).</summary>
    public static string CurrentVersion =>
        typeof(AppInstall).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } v
            ? v.Split('+')[0]
            : typeof(AppInstall).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>A Karvon Studio in a folder: what its marker file says it is (an AppImage's, when it runs from one).</summary>
    public static AppInstall Of(string directory, string version, string? appImage = null)
    {
        var marker = Path.Combine(directory, MarkerFile);
        var said = File.Exists(marker) ? File.ReadAllText(marker).Trim().ToLowerInvariant() : "";
        var distribution = said switch
        {
            "deb" => Distribution.Deb,
            "appimage" when appImage is { Length: > 0 } => Distribution.AppImage,
            "tar" => Distribution.Tar,
            "dmg" => Distribution.Dmg,
            "setup" => Distribution.Setup,
            "zip" => Distribution.Zip,
            _ => Distribution.Source,
        };
        return new AppInstall(distribution, version, Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            distribution == Distribution.AppImage ? appImage : null);
    }

    /// <summary>Whether it updates itself (a build of the sources does not).</summary>
    public bool CanUpdate => Distribution != Distribution.Source;

    /// <summary>What it is, as the installer says it.</summary>
    public string Description => Distribution switch
    {
        Distribution.Deb => "Debian package",
        Distribution.AppImage => "AppImage",
        Distribution.Tar => "Linux .tar.gz",
        Distribution.Dmg => "macOS app",
        Distribution.Setup => "Windows installer",
        Distribution.Zip => "Windows .zip",
        _ => "built from the sources",
    };

    /// <summary>A Mac's architecture, as the .dmg's name says it.</summary>
    static string MacArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The release's file this Karvon Studio updates from (as the release workflow names them).</summary>
    public string? AssetName(string version) => Distribution switch
    {
        Distribution.Deb => $"karvon-studio_{version}_amd64.deb",
        Distribution.AppImage => $"Karvon-Studio-{version}-x86_64.AppImage",
        Distribution.Tar => $"karvon-studio-{version}-linux-x64.tar.gz",
        Distribution.Dmg => $"Karvon-Studio-{version}-{MacArchitecture}.dmg",
        Distribution.Setup => $"Karvon-Studio-{version}-Setup.exe",
        Distribution.Zip => $"karvon-studio-{version}-win-x64.zip",
        _ => null,
    };

    /// <summary>The app's bundle (Karvon Studio.app), of a Mac's: the program is in its Contents/MacOS.</summary>
    public string Bundle => Path.GetDirectoryName(Path.GetDirectoryName(Directory)!)!;

    /// <summary>
    /// Karvon Studio started again (as this one exits), with the arguments it had: its AppImage's file (the mounted one goes
    /// as it exits), a Mac's bundle by open, else its program; a build of the sources as it was started (dotnet Karvon.dll).
    /// </summary>
    public void StartAgain(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start;
        if (Distribution == Distribution.Dmg)
            start = new ProcessStartInfo("open", ["-n", Bundle, .. arguments.Count > 0 ? ["--args", .. arguments] : Array.Empty<string>()]);
        else if (Distribution != Distribution.Source)
            start = new ProcessStartInfo(Program, arguments);
        else
        {
            var process = Environment.ProcessPath ?? Program;
            start = Path.GetFileNameWithoutExtension(process) == "dotnet" && Environment.GetCommandLineArgs() is [var dll, ..]
                ? new ProcessStartInfo(process, [dll, .. arguments])
                : new ProcessStartInfo(process, arguments);
        }
        start.UseShellExecute = false;
        // A build of the sources starts again by dotnet, a console program: no console window with it.
        start.CreateNoWindow = true;
        Process.Start(start)?.Dispose();
    }

    /// <summary>The program that starts it again.</summary>
    public string Program => Distribution switch
    {
        Distribution.AppImage => AppImage!,
        _ => Path.Combine(Directory, OperatingSystem.IsWindows() ? "Karvon.exe" : "Karvon"),
    };
}
