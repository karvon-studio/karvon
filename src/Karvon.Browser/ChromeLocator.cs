namespace Karvon.Browser;

/// <summary>
/// The Chrome Karvon drives: KARVON_CHROME_PATH's ("off": none), else Google Chrome, Chromium or Microsoft Edge where
/// each installs itself.
/// </summary>
public static class ChromeLocator
{
    static readonly string[] OnPath = ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable"];

    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_CHROME_PATH") is { Length: > 0 } given)
            return given != "off" && File.Exists(given) ? given : null;
        IEnumerable<string> candidates;
        if (OperatingSystem.IsWindows())
        {
            string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)];
            candidates = roots.Where(r => r.Length > 0).SelectMany(r => new[]
            {
                Path.Combine(r, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(r, "Chromium", "Application", "chrome.exe"),
                Path.Combine(r, "Microsoft", "Edge", "Application", "msedge.exe"),
            });
        }
        else if (OperatingSystem.IsMacOS())
            candidates = ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Google Chrome.app/Contents/MacOS/Google Chrome")];
        else
            candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(dir => OnPath.Select(name => Path.Combine(dir, name)));
        return candidates.FirstOrDefault(File.Exists);
    }
}
