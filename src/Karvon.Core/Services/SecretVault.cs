using System.Security.Cryptography;
using System.Text;
using Bengine.Introspection.Servers;
using Microsoft.Data.Sqlite;

namespace Karvon.Services;

/// <summary>
/// Karvon's secrets in a database of its own (vault.db in Karvon's settings folder, the user's alone), each encrypted
/// with AES-256-GCM under a key of the vault's and bound to its name, so that the file tells nothing and an entry moved
/// to another name does not open. The vault's key is the one secret the system keyring keeps for Karvon; what the
/// keyring kept before is moved into the vault the first time it is asked for, and taken out of the keyring.
/// With a passphrase the key is not in the keyring at all: it is kept in the vault wrapped by a key drawn from the
/// passphrase (PBKDF2-SHA256), and the vault stays locked (nothing in it read or kept) until Karvon is given it as it
/// starts. Claude's tools are kept from the file and the key (<see cref="Karvon.Claude.SecretFiles"/>,
/// <see cref="Karvon.Claude.SecretGuard"/>); what keeps any process of the user's from them is the sandbox.
/// Its sealed part (<see cref="SealedStore"/>) has a key of its own, made when there is a passphrase and only ever kept
/// wrapped by it: what the keyring gave before, or gives now, opens nothing there.
/// </summary>
public sealed class SecretVault : ICredentialStore
{
    /// <summary>The keyring's entry that holds the vault's key.</summary>
    public const string KeyName = "karvon-vault/key";

    const string KeyLabel = "Karvon: the key of Karvon's vault";
    const int NonceSize = 12;
    const int TagSize = 16;

    /// <summary>PBKDF2-SHA256's rounds for a passphrase (OWASP's for it).</summary>
    public const int Iterations = 600_000;

    /// <summary>The fewest characters a passphrase has.</summary>
    public const int MinPassphrase = 8;

    readonly ICredentialStore _keyring;
    readonly string _path;
    byte[]? _key, _sealKey;
    readonly Lock _lock = new();

    /// <summary>It was opened, or its passphrase was set, changed, taken away or forgotten.</summary>
    public event Action? Changed;

    SecretVault(ICredentialStore keyring, string path)
    {
        _keyring = keyring;
        _path = path;
        Execute("CREATE TABLE IF NOT EXISTS secrets (name TEXT PRIMARY KEY, label TEXT NOT NULL, nonce BLOB NOT NULL, value BLOB NOT NULL, updated TEXT NOT NULL)");
        Execute("CREATE TABLE IF NOT EXISTS sealed (name TEXT PRIMARY KEY, label TEXT NOT NULL, nonce BLOB NOT NULL, value BLOB NOT NULL, updated TEXT NOT NULL)");
        // The vault's own: its key wrapped by the passphrase, when it has one.
        Execute("CREATE TABLE IF NOT EXISTS vault (name TEXT PRIMARY KEY, value BLOB NOT NULL)");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// The vault at <paramref name="path"/>: locked when it has a passphrase, else its key the keyring's (made the first
    /// time); null when the keyring cannot keep a new one.
    /// </summary>
    public static SecretVault? Open(ICredentialStore keyring, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var vault = new SecretVault(keyring, path);
        if (vault.HasPassphrase)
            return vault;
        var key = keyring.Get(KeyName) is { } kept && TryKey(kept) is { } existing ? existing : null;
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            if (!keyring.Set(KeyName, KeyLabel, Convert.ToBase64String(key)))
                return null;
        }
        vault.Opened(key);
        return vault;
    }

    void Opened(byte[] key)
    {
        _key = key;
        Karvon.Claude.Tools.KnownSecrets.Add(KeyName, Convert.ToBase64String(key));
    }

    /// <summary>Nothing in it can be read or kept yet: its passphrase has not been given.</summary>
    public bool IsLocked => _key is null;

    /// <summary>Its key is kept wrapped by a passphrase, not in the keyring.</summary>
    public bool HasPassphrase => Meta("wrapped") is not null;

    /// <summary>Opens it with its passphrase; false when it is not the vault's.</summary>
    public bool Unlock(string passphrase)
    {
        if (!IsLocked)
            return true;
        if (Meta("wrapped") is not { } wrapped || Meta("salt") is not { } salt || Meta("iterations") is not { } rounds)
            return false;
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, int.Parse(Encoding.UTF8.GetString(rounds)), HashAlgorithmName.SHA256, 32);
        if (Unwrap(kek, wrapped, KeyName) is not { } key)
            return false;
        Karvon.Claude.Tools.KnownSecrets.Seal("karvon-vault/passphrase", passphrase);
        // A vault whose passphrase is older than its sealed part gets one now.
        _sealKey = Meta(SealMeta) is { } seal ? Unwrap(kek, seal, SealMeta) : null;
        if (_sealKey is null)
        {
            _sealKey = RandomNumberGenerator.GetBytes(32);
            SetMeta(SealMeta, Wrap(kek, _sealKey, SealMeta));
        }
        Opened(key);
        Changed?.Invoke();
        return true;
    }

    const string SealMeta = "sealed-key";

    static byte[] Wrap(byte[] kek, byte[] key, string name)
    {
        var wrapped = new byte[NonceSize + 32 + TagSize];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceSize));
        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(wrapped.AsSpan(0, NonceSize), key, wrapped.AsSpan(NonceSize, 32), wrapped.AsSpan(NonceSize + 32), Encoding.UTF8.GetBytes(name));
        return wrapped;
    }

    static byte[]? Unwrap(byte[] kek, byte[] wrapped, string name)
    {
        var key = new byte[32];
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(wrapped.AsSpan(0, NonceSize), wrapped.AsSpan(NonceSize, 32), wrapped.AsSpan(NonceSize + 32), key, Encoding.UTF8.GetBytes(name));
            return key;
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Keeps its key wrapped by <paramref name="passphrase"/> (a new one, or another) and takes it out of the keyring.</summary>
    public void SetPassphrase(string passphrase)
    {
        var key = _key ?? throw new InvalidOperationException("The vault is locked.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        _sealKey ??= RandomNumberGenerator.GetBytes(32);
        lock (_lock)
        {
            SetMeta("salt", salt);
            SetMeta("iterations", Encoding.UTF8.GetBytes(Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            SetMeta("wrapped", Wrap(kek, key, KeyName));
            SetMeta(SealMeta, Wrap(kek, _sealKey, SealMeta));
        }
        Karvon.Claude.Tools.KnownSecrets.Seal("karvon-vault/passphrase", passphrase);
        _keyring.Delete(KeyName);
        Changed?.Invoke();
    }

    /// <summary>Its key back in the keyring, the passphrase gone, and with it what was sealed; false when the keyring cannot keep the key.</summary>
    public bool RemovePassphrase()
    {
        var key = _key ?? throw new InvalidOperationException("The vault is locked.");
        if (!_keyring.Set(KeyName, KeyLabel, Convert.ToBase64String(key)))
            return false;
        lock (_lock)
        {
            Execute("DELETE FROM vault");
            Execute("DELETE FROM sealed");
        }
        _sealKey = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// A passphrase forgotten: what the vault kept goes (each sign-in is asked again), and a new key is kept in the keyring;
    /// false when the keyring cannot keep it (the vault stays locked).
    /// </summary>
    public bool Forget()
    {
        lock (_lock)
        {
            Execute("DELETE FROM secrets");
            Execute("DELETE FROM sealed");
            Execute("DELETE FROM vault");
        }
        var key = RandomNumberGenerator.GetBytes(32);
        if (!_keyring.Set(KeyName, KeyLabel, Convert.ToBase64String(key)))
            return false;
        _sealKey = null;
        Opened(key);
        Changed?.Invoke();
        return true;
    }

    // ---- What is sealed ---------------------------------------------------------------------------------------

    /// <summary>It has a passphrase and is open: what is sealed is read and kept.</summary>
    public bool CanSeal => _sealKey is not null && !IsLocked;

    /// <summary>How many things are sealed (they go with the passphrase).</summary>
    public int SealedCount
    {
        get
        {
            lock (_lock)
            {
                using var connection = Connect();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sealed";
                return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    public string? GetSealed(string name) => _sealKey is { } key ? Read("sealed", key, name) : null;

    public bool SetSealed(string name, string label, string secret) => _sealKey is { } key && Write("sealed", key, name, label, secret);

    public void DeleteSealed(string name)
    {
        lock (_lock)
            Execute("DELETE FROM sealed WHERE name = $name", ("$name", name));
    }

    byte[]? Meta(string name)
    {
        lock (_lock)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM vault WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() as byte[];
        }
    }

    void SetMeta(string name, byte[] value) =>
        Execute("INSERT INTO vault (name, value) VALUES ($name, $value) ON CONFLICT(name) DO UPDATE SET value = $value", ("$name", name), ("$value", value));

    static byte[]? TryKey(string text)
    {
        try
        {
            return Convert.FromBase64String(text) is { Length: 32 } key ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string Name => $"Karvon's vault ({_keyring.Name})";

    public string? Get(string key)
    {
        if (IsLocked)
            return null;
        if (Read("secrets", _key!, key) is { } secret)
            return secret;
        // Kept in the keyring before the vault: moved into it.
        if (key == KeyName || _keyring.Get(key) is not { } legacy)
            return null;
        if (Write("secrets", _key!, key, key, legacy))
            _keyring.Delete(key);
        return legacy;
    }

    public bool Set(string key, string label, string secret) => key != KeyName && !IsLocked && Write("secrets", _key!, key, label, secret);

    public void Delete(string key)
    {
        if (key == KeyName)
            return;
        lock (_lock)
            Execute("DELETE FROM secrets WHERE name = $name", ("$name", key));
        _keyring.Delete(key);
    }

    string? Read(string table, byte[] key, string name)
    {
        lock (_lock)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT nonce, value FROM {table} WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            var nonce = (byte[])reader[0];
            var sealedValue = (byte[])reader[1];
            if (sealedValue.Length < TagSize)
                return null;
            var plain = new byte[sealedValue.Length - TagSize];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, sealedValue.AsSpan(0, plain.Length), sealedValue.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(name));
            }
            catch (AuthenticationTagMismatchException)
            {
                // Not this vault's key, or not this entry's name: as if there were none.
                return null;
            }
            return Encoding.UTF8.GetString(plain);
        }
    }

    bool Write(string table, byte[] key, string name, string label, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var sealedValue = new byte[plain.Length + TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, sealedValue.AsSpan(0, plain.Length), sealedValue.AsSpan(plain.Length), Encoding.UTF8.GetBytes(name));
        lock (_lock)
            Execute($"INSERT INTO {table} (name, label, nonce, value, updated) VALUES ($name, $label, $nonce, $value, $updated) "
                + "ON CONFLICT(name) DO UPDATE SET label = $label, nonce = $nonce, value = $value, updated = $updated",
                ("$name", name), ("$label", label), ("$nonce", nonce), ("$value", sealedValue), ("$updated", DateTime.UtcNow.ToString("O")));
        return true;
    }

    SqliteConnection Connect()
    {
        // Pooling off: the file is not held open by Karvon between reads (nor for a copy of it to be taken from memory).
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
