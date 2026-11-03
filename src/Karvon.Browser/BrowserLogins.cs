using System.Text.Json;
using Bengine.Introspection.Servers;

namespace Karvon.Browser;

/// <summary>A sign-in Karvon keeps for a site: its origin and the user's name (the password is in the vault).</summary>
public sealed record BrowserLogin(string Origin, string Username)
{
    public string Host => Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Authority : Origin;
}

/// <summary>
/// The sign-ins Karvon fills into a site's page for Claude (browser_login) without Claude seeing the password: each
/// site and name in a list of Karvon's, each password in Karvon's vault (the system keyring under it).
/// </summary>
public sealed class BrowserLogins(string file, ICredentialStore? store)
{
    List<BrowserLogin>? _all;

    public event Action? Changed;

    /// <summary>Whether passwords can be kept (there is a vault or a keyring).</summary>
    public bool CanKeep => store is not null;

    public IReadOnlyList<BrowserLogin> All => _all ??= Read();

    List<BrowserLogin> Read()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<BrowserLogin>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }

    /// <summary>A site as Karvon keys its sign-ins: its origin (https:// when no scheme is given); null for none.</summary>
    public static string? OriginOf(string site)
    {
        site = site.Trim();
        if (site.Length == 0)
            return null;
        if (!site.Contains("://", StringComparison.Ordinal))
            site = "https://" + site;
        return ManagedBrowser.OriginOf(site);
    }

    static string Key(BrowserLogin login) => $"karvon-browser/{login.Origin}/{login.Username}";

    /// <summary>A sign-in kept (its password replaced when it is there already).</summary>
    public BrowserLogin Save(string site, string username, string password)
    {
        var origin = OriginOf(site) ?? throw new InvalidOperationException($"{site} is not a site's address (https://example.com).");
        if (store is null)
            throw new InvalidOperationException("Karvon has no vault or keyring to keep a password in.");
        var login = new BrowserLogin(origin, username.Trim());
        if (!store.Set(Key(login), $"Karvon browser: {login.Host} ({login.Username})", password))
            throw new InvalidOperationException("The vault did not keep the password (it may be locked: Tools > Vault > Unlock).");
        _all = [.. All.Where(l => l != login), login];
        Write();
        return login;
    }

    public void Remove(BrowserLogin login)
    {
        store?.Delete(Key(login));
        _all = [.. All.Where(l => l != login)];
        Write();
    }

    /// <summary>The sign-ins of a page's origin (the same host on another port or scheme too, its own first).</summary>
    public IReadOnlyList<BrowserLogin> For(string? origin)
    {
        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return [];
        return [.. All.Where(l => l.Origin == origin), .. All.Where(l => l.Origin != origin && Uri.TryCreate(l.Origin, UriKind.Absolute, out var o)
            && string.Equals(o.Host, uri.Host, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The password, for Karvon to type into the page; never for Claude.</summary>
    internal string? Password(BrowserLogin login) => store?.Get(Key(login));
}
