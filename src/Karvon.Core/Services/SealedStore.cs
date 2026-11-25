using System.Collections.Concurrent;

namespace Karvon.Services;

/// <summary>
/// What no agent may get, and so nothing an agent can run may either: kept in the vault's sealed part, whose key only the
/// vault's passphrase opens (it is in no keyring and no file); without a passphrase, in memory for this session only.
/// </summary>
public sealed class SealedStore(Func<SecretVault?> vault)
{
    public static SealedStore Karvons { get; } = new(() => Keyring.Vault);

    /// <summary>What the vault has not yet, or cannot keep; an empty one is one taken away.</summary>
    readonly ConcurrentDictionary<string, string> _session = new(StringComparer.Ordinal);

    readonly Lock _lock = new();
    Task _writes = Task.CompletedTask;

    /// <summary>The vault itself: told when it is opened, or its passphrase set or taken away.</summary>
    public SecretVault? Vault => vault();

    /// <summary>What is kept now outlives this session: the vault has a passphrase and is open.</summary>
    public bool CanKeep => vault() is { CanSeal: true };

    /// <summary>The vault has a passphrase that was not given yet: what it keeps is not here.</summary>
    public bool IsLocked => vault() is { IsLocked: true };

    public string? Get(string name) => _session.TryGetValue(name, out var value) ? value.Length > 0 ? value : null : Kept(name);

    /// <summary>What the vault keeps, whatever the session has over it.</summary>
    public string? Kept(string name) => vault() is { CanSeal: true } open ? open.GetSealed(name) : null;

    public void Set(string name, string label, string value)
    {
        _session[name] = value;
        Write(name, label);
    }

    public void Delete(string name)
    {
        _session[name] = "";
        Write(name, "");
    }

    /// <summary>The last write (the disk's: never on the window's thread).</summary>
    public Task Written
    {
        get
        {
            lock (_lock)
                return _writes;
        }
    }

    void Write(string name, string label)
    {
        lock (_lock)
            _writes = _writes.ContinueWith(_ =>
            {
                if (vault() is not { CanSeal: true } open || !_session.TryGetValue(name, out var value))
                    return;
                try
                {
                    if (value.Length == 0)
                        open.DeleteSealed(name);
                    else if (!open.SetSealed(name, label, value))
                        return;
                    // Unless it was set again meanwhile.
                    ((ICollection<KeyValuePair<string, string>>)_session).Remove(new(name, value));
                }
                // The disk's: it is this session's.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                }
            }, TaskScheduler.Default);
    }
}
