namespace Karvon.Lsp;

/// <summary>file: URIs as the platform's paths.</summary>
public static class FileUris
{
    public static string ToPath(string uri) => ToPath(new Uri(uri));

    /// <summary>
    /// A file: URI's path: on Windows its drive's letter capital and its separators backslashes, its colon read when a
    /// server escapes it (Roslyn's file:///c%3A/...), which the URI's own LocalPath leaves as /c:/...
    /// </summary>
    public static string ToPath(Uri uri)
    {
        var path = uri.LocalPath;
        if (!OperatingSystem.IsWindows())
            return path;
        if (path.Length >= 3 && path[0] is '/' or '\\' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..];
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];
        return path.Replace('/', '\\');
    }
}
