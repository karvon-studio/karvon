namespace Karvon.Lsp;

/// <summary>
/// The files a language server serves, whether one is running yet or not: the editor waits for the server's colors and
/// blocks there instead of keeping its own.
/// </summary>
public static class LanguageServers
{
    static readonly List<Func<string, bool>> Served = [RoslynClient.Serves];

    /// <summary>A workload's language server, by the files it serves.</summary>
    public static void Register(Func<string, bool> serves)
    {
        lock (Served)
            Served.Add(serves);
    }

    public static bool Serve(string path)
    {
        lock (Served)
            return Served.Any(s => s(path));
    }
}
