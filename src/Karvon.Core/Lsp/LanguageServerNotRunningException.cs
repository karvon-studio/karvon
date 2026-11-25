namespace Karvon.Lsp;

/// <summary>A file's language server is not running (and was not to be started): what it asked for waits until it does.</summary>
public sealed class LanguageServerNotRunningException(string server) : InvalidOperationException($"{server} is not running.")
{
    public string Server { get; } = server;
}
