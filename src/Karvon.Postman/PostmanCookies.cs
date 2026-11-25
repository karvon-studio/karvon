using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;
using Karvon.Services;

namespace Karvon.Postman;

/// <summary>A cookie a server set (or a script, or the user).</summary>
public sealed record PostmanCookie(string Name, string Value, string Domain, string Path)
{
    /// <summary>For its domain alone, not those under it.</summary>
    public bool HostOnly { get; init; } = true;

    public DateTimeOffset? Expires { get; init; }

    public bool Secure { get; init; }

    public bool HttpOnly { get; init; }

    public bool IsExpired => Expires is { } at && at <= DateTimeOffset.UtcNow;

    public bool Matches(Uri uri) => !IsExpired && (!Secure || uri.Scheme == "https" || uri.IsLoopback)
        && (uri.Host.Equals(Domain, StringComparison.OrdinalIgnoreCase) || (!HostOnly && uri.Host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase)))
        && (uri.AbsolutePath == Path || (uri.AbsolutePath.StartsWith(Path, StringComparison.Ordinal) && (Path.EndsWith('/') || uri.AbsolutePath.Length > Path.Length && uri.AbsolutePath[Path.Length] == '/')));
}

/// <summary>
/// The cookies servers set, sent back to them as Postman does. The user's are kept as an environment's values are (the
/// vault's sealed part, or this session); an agent's calls have their own, which are not the user's.
/// </summary>
public sealed class PostmanCookies(SealedStore? store)
{
    const string StoreKey = "postman/cookies";

    readonly Lock _lock = new();
    List<PostmanCookie>? _cookies;

    public event Action? Changed;

    List<PostmanCookie> Own
    {
        get
        {
            if (_cookies is null)
            {
                _cookies = [];
                try
                {
                    if (store?.Get(StoreKey) is { Length: > 0 } kept)
                        _cookies = JsonSerializer.Deserialize<List<PostmanCookie>>(kept) ?? [];
                }
                catch (JsonException)
                {
                }
                _cookies.RemoveAll(c => c.IsExpired);
                Hold();
            }
            return _cookies;
        }
    }

    public IReadOnlyList<PostmanCookie> All
    {
        get
        {
            lock (_lock)
                return [.. Own.Where(c => !c.IsExpired).OrderBy(c => c.Domain, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.Ordinal)];
        }
    }

    /// <summary>The cookies a request to <paramref name="uri"/> has, the longer paths first.</summary>
    public IReadOnlyList<PostmanCookie> For(Uri uri)
    {
        lock (_lock)
            return [.. Own.Where(c => c.Matches(uri)).OrderByDescending(c => c.Path.Length)];
    }

    /// <summary>The vault was opened: what it keeps and is not here is.</summary>
    public void Reopen()
    {
        lock (_lock)
        {
            var session = _cookies ?? [];
            _cookies = null;
            foreach (var cookie in session)
                Put(cookie);
            Keep();
        }
        Changed?.Invoke();
    }

    public void Set(PostmanCookie cookie)
    {
        lock (_lock)
        {
            Put(cookie);
            Keep();
        }
        Changed?.Invoke();
    }

    void Put(PostmanCookie cookie)
    {
        Own.RemoveAll(c => c.Name == cookie.Name && c.Path == cookie.Path && c.Domain.Equals(cookie.Domain, StringComparison.OrdinalIgnoreCase));
        if (!cookie.IsExpired)
            Own.Add(cookie);
    }

    public void Remove(Func<PostmanCookie, bool> which)
    {
        lock (_lock)
        {
            if (Own.RemoveAll(c => which(c)) == 0)
                return;
            Keep();
        }
        Changed?.Invoke();
    }

    /// <summary>What a response set (its Set-Cookie headers).</summary>
    public void Read(Uri uri, IEnumerable<string> headers)
    {
        var any = false;
        lock (_lock)
        {
            foreach (var header in headers)
                if (Parse(uri, header) is { } cookie)
                {
                    Put(cookie);
                    any = true;
                }
            if (any)
                Keep();
        }
        if (any)
            Changed?.Invoke();
    }

    /// <summary>A Set-Cookie header (RFC 6265); null for one the host may not set.</summary>
    public static PostmanCookie? Parse(Uri uri, string header)
    {
        var parts = header.Split(';');
        if (parts[0].Split('=', 2) is not [var name, var value] || name.Trim().Length == 0)
            return null;
        var slash = uri.AbsolutePath.LastIndexOf('/');
        var cookie = new PostmanCookie(name.Trim(), value.Trim(), uri.Host, slash <= 0 ? "/" : uri.AbsolutePath[..slash]);
        DateTimeOffset? maxAge = null;
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('=', 2);
            var said = pair.Length == 2 ? pair[1].Trim() : "";
            switch (pair[0].Trim().ToLowerInvariant())
            {
                case "domain" when said.TrimStart('.') is { Length: > 0 } domain:
                    // Its own domain, or one above it (not a whole top-level one).
                    if (!uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) && !(uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase) && domain.Contains('.')))
                        return null;
                    cookie = cookie with { Domain = domain, HostOnly = false };
                    break;
                case "path" when said.StartsWith('/'):
                    cookie = cookie with { Path = said };
                    break;
                case "expires" when DateTimeOffset.TryParse(said.Replace('-', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at):
                    cookie = cookie with { Expires = at };
                    break;
                case "max-age" when long.TryParse(said, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds):
                    maxAge = seconds <= 0 ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 400L * 24 * 3600));
                    break;
                case "secure":
                    cookie = cookie with { Secure = true };
                    break;
                case "httponly":
                    cookie = cookie with { HttpOnly = true };
                    break;
            }
        }
        return maxAge is null ? cookie : cookie with { Expires = maxAge };
    }

    void Keep()
    {
        Hold();
        if (store is null)
            return;
        if (Own.Count == 0)
            store.Delete(StoreKey);
        else
            store.Set(StoreKey, "Karvon: Postman's cookies", JsonSerializer.Serialize(Own));
    }

    /// <summary>The user's are hidden from agents as an environment's secrets are.</summary>
    void Hold()
    {
        if (store is not null)
            foreach (var cookie in Own.Where(c => PostmanEnvironment.LooksSecret(c.Value)))
                KnownSecrets.Seal($"Postman cookie {cookie.Domain}/{cookie.Name}", cookie.Value);
    }
}
