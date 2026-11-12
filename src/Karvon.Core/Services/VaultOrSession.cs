using System.Collections.Concurrent;
using Bengine.Introspection.Servers;

namespace Karvon.Services;

/// <summary>
/// The store for a list that keeps a password in its own file when its store does not take it (bengine's server list):
/// a secret goes to Karvon's vault (or the keyring) when it can be kept there, else it is kept for this session only and
/// asked again the next time Karvon starts. It never reaches a file: the list always reads that it is kept here.
/// </summary>
public sealed class VaultOrSession(ICredentialStore? kept, Func<bool>? canKeep = null) : ICredentialStore
{
    readonly ConcurrentDictionary<string, string> _session = new(StringComparer.Ordinal);

    /// <summary>Over Karvon's own store: the vault (while it is not locked) or the keyring.</summary>
    public static VaultOrSession OverKarvons() => new(Keyring.System, () => Keyring.Vault is not { IsLocked: true });

    /// <summary>A secret given now outlives this session: there is a vault, not locked, or a keyring.</summary>
    public bool CanKeep => kept is not null && (canKeep?.Invoke() ?? true);

    public string Name => CanKeep ? kept!.Name : "memory of this session";

    public string? Get(string key) => _session.TryGetValue(key, out var secret) ? secret : kept?.Get(key);

    public bool Set(string key, string label, string secret)
    {
        Karvon.Claude.Tools.KnownSecrets.Add(key, secret);
        bool done;
        try
        {
            done = CanKeep && kept!.Set(key, label, secret);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            done = false;
        }
        if (done)
            _session.TryRemove(key, out _);
        else
            _session[key] = secret;
        return true;
    }

    public void Delete(string key)
    {
        _session.TryRemove(key, out _);
        try
        {
            kept?.Delete(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A keyring that cannot be reached now: what it keeps is found again as before (the list asks for it by id).
        }
    }
}
