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
/// Claude's tools are kept from the file and the key (<see cref="Karvon.Claude.SecretFiles"/>,
/// <see cref="Karvon.Claude.SecretGuard"/>); what keeps any process of the user's from them is the sandbox.
/// </summary>
public sealed class SecretVault : ICredentialStore
{
    /// <summary>The keyring's entry that holds the vault's key.</summary>
    public const string KeyName = "karvon-vault/key";

    const int NonceSize = 12;
    const int TagSize = 16;

    readonly ICredentialStore _keyring;
    readonly string _path;
    readonly byte[] _key;
    readonly Lock _lock = new();

    SecretVault(ICredentialStore keyring, string path, byte[] key)
    {
        _keyring = keyring;
        _path = path;
        _key = key;
        Execute("CREATE TABLE IF NOT EXISTS secrets (name TEXT PRIMARY KEY, label TEXT NOT NULL, nonce BLOB NOT NULL, value BLOB NOT NULL, updated TEXT NOT NULL)");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The vault at <paramref name="path"/>, its key the keyring's (made the first time); null when the keyring cannot keep it.</summary>
    public static SecretVault? Open(ICredentialStore keyring, string path)
    {
        var key = keyring.Get(KeyName) is { } kept && TryKey(kept) is { } existing ? existing : null;
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            if (!keyring.Set(KeyName, "Karvon: the key of Karvon's vault", Convert.ToBase64String(key)))
                return null;
        }
        Karvon.Claude.Tools.KnownSecrets.Add(KeyName, Convert.ToBase64String(key));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new SecretVault(keyring, path, key);
    }

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
        if (Read(key) is { } secret)
            return secret;
        // Kept in the keyring before the vault: moved into it.
        if (key == KeyName || _keyring.Get(key) is not { } legacy)
            return null;
        if (Write(key, key, legacy))
            _keyring.Delete(key);
        return legacy;
    }

    public bool Set(string key, string label, string secret) => key != KeyName && Write(key, label, secret);

    public void Delete(string key)
    {
        if (key == KeyName)
            return;
        lock (_lock)
            Execute("DELETE FROM secrets WHERE name = $name", ("$name", key));
        _keyring.Delete(key);
    }

    string? Read(string name)
    {
        lock (_lock)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT nonce, value FROM secrets WHERE name = $name";
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
                using var aes = new AesGcm(_key, TagSize);
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

    bool Write(string name, string label, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var sealedValue = new byte[plain.Length + TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plain, sealedValue.AsSpan(0, plain.Length), sealedValue.AsSpan(plain.Length), Encoding.UTF8.GetBytes(name));
        lock (_lock)
            Execute("INSERT INTO secrets (name, label, nonce, value, updated) VALUES ($name, $label, $nonce, $value, $updated) "
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
