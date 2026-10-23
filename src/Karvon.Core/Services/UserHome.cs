namespace Karvon.Services;

/// <summary>The home the user's tools keep their files in (agents' settings, user secrets).</summary>
public static class UserHome
{
    /// <summary>KARVON_USER_HOME (the tests'), else the user's home.</summary>
    public static string Path => Environment.GetEnvironmentVariable("KARVON_USER_HOME") is { Length: > 0 } home ? home
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Windows' %APPDATA% (AppData\Roaming in the tests' home).</summary>
    public static string WindowsAppData => Environment.GetEnvironmentVariable("KARVON_USER_HOME") is { Length: > 0 } home
        ? System.IO.Path.Combine(home, "AppData", "Roaming")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
}
