using System.Text;
using Bengine.Introspection.Servers;
using Tmds.DBus.Protocol;

namespace Karvon.Services;

/// <summary>
/// The system keyring Karvon Studio keeps its tokens and passwords in: the Secret Service (GNOME Keyring, KWallet) on
/// Linux, the Credential Manager on Windows and the Keychain on macOS; null when there is none to reach (a headless
/// machine) or <c>BENGINE_KEYRING=off</c>. Not bengine's <see cref="CredentialStores.System"/> on Linux: bengine's
/// Secret Service store is built against the Tmds.DBus.Protocol of Bengine Studio's Avalonia 11, and the one Avalonia 12
/// brings here has no type it uses, so it never loaded and every secret was asked again each session.
/// </summary>
public static class Keyring
{
    /// <summary>Karvon's vault over the system keyring (the keyring itself when the vault cannot be opened), each secret made known.</summary>
    static readonly Lazy<ICredentialStore?> Detected = new(() => Detect() is { } store ? new Remembering(VaultOver(store)) : PassphraseOnly());

    static string VaultFile => Path.Combine(Settings.ConfigDirectory, "vault.db");

    /// <summary>No keyring to reach (a headless machine): a vault with a passphrase needs none.</summary>
    static ICredentialStore? PassphraseOnly()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("BENGINE_KEYRING"), "off", StringComparison.OrdinalIgnoreCase) || !File.Exists(VaultFile))
            return null;
        try
        {
            return SecretVault.Open(NoKeyring.Instance, VaultFile) is { HasPassphrase: true } vault ? new Remembering(_vault = vault) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    sealed class NoKeyring : ICredentialStore
    {
        public static readonly NoKeyring Instance = new();

        public string Name => "no keyring";

        public string? Get(string key) => null;

        public bool Set(string key, string label, string secret) => false;

        public void Delete(string key)
        {
        }
    }

    static ICredentialStore VaultOver(ICredentialStore keyring)
    {
        try
        {
            return (_vault = SecretVault.Open(keyring, VaultFile)) ?? (ICredentialStore)keyring;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return keyring;
        }
    }

    public static ICredentialStore? System => Detected.Value;

    static SecretVault? _vault;

    /// <summary>Karvon's vault, when there is one (a keyring to keep its key, or a passphrase); locked until its passphrase is given.</summary>
    public static SecretVault? Vault
    {
        get
        {
            _ = Detected.Value;
            return _vault;
        }
    }

    /// <summary>The store, with each secret it gives or keeps made known (<see cref="Karvon.Claude.Tools.KnownSecrets"/>), so none reaches Claude.</summary>
    sealed class Remembering(ICredentialStore store) : ICredentialStore
    {
        public string Name => store.Name;

        public string? Get(string key)
        {
            var secret = store.Get(key);
            Karvon.Claude.Tools.KnownSecrets.Add(key, secret);
            return secret;
        }

        public bool Set(string key, string label, string secret)
        {
            Karvon.Claude.Tools.KnownSecrets.Add(key, secret);
            return store.Set(key, label, secret);
        }

        public void Delete(string key) => store.Delete(key);
    }

    static ICredentialStore? Detect()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("BENGINE_KEYRING"), "off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!OperatingSystem.IsLinux())
            return CredentialStores.System;
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } bus)
            return null;
        try
        {
            // Probe once: a store that cannot answer a lookup is not usable.
            var store = new SecretServiceKeyring(bus);
            store.Get("bengine-probe");
            return store;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// The freedesktop Secret Service over the session D-Bus, with the "plain" session algorithm (the secret only crosses
/// the local bus to the user's own keyring daemon). Its items are bengine's (<c>application=bengine</c>, <c>key=…</c>),
/// so what Bengine Studio and the Database Explorer kept is found. A locked collection that would need a prompt is
/// treated as unavailable.
/// </summary>
public sealed class SecretServiceKeyring(string address) : ICredentialStore
{
    const string Bus = "org.freedesktop.secrets";
    const string ServicePath = "/org/freedesktop/secrets";
    const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
    const string Application = "bengine";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "Secret Service (GNOME Keyring / KWallet)";

    public string? Get(string key) => Run(async connection =>
    {
        var session = await OpenSessionAsync(connection);
        var (unlocked, locked) = await SearchAsync(connection, key);
        if (unlocked.Length == 0)
            return locked.Length == 0 ? null : throw new InvalidOperationException("The keyring is locked.");
        return await GetSecretAsync(connection, unlocked[0], session);
    });

    public bool Set(string key, string label, string secret) => Run(async connection =>
    {
        var session = await OpenSessionAsync(connection);
        var prompt = await connection.CallMethodAsync(CreateItem(connection, session, key, label, secret), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadObjectPath();
            return reader.ReadObjectPathAsString();
        }, null);
        return prompt == "/";
    });

    public void Delete(string key) => Run(async connection =>
    {
        var (unlocked, _) = await SearchAsync(connection, key);
        foreach (var item in unlocked)
            await connection.CallMethodAsync(DeleteItem(connection, item));
        return 0;
    });

    static MessageBuffer CreateItem(DBusConnection connection, string session, string key, string label, string secret)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b", MessageFlags.None);
        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString(label);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteVariant(new Dict<string, string> { ["application"] = Application, ["key"] = key }.AsVariantValue());
        writer.WriteDictionaryEnd(properties);
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(Encoding.UTF8.GetBytes(secret));
        writer.WriteString("text/plain; charset=utf8");
        writer.WriteBool(true);
        return writer.CreateMessage();
    }

    static MessageBuffer DeleteItem(DBusConnection connection, string item)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, item, "org.freedesktop.Secret.Item", "Delete", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    T Run<T>(Func<DBusConnection, Task<T>> work) => Task.Run(async () =>
    {
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        var call = work(connection);
        if (await Task.WhenAny(call, Task.Delay(Timeout)) != call)
            throw new TimeoutException("The keyring did not answer.");
        return await call;
    }).GetAwaiter().GetResult();

    static Task<string> OpenSessionAsync(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "OpenSession", "sv", MessageFlags.None);
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null);
    }

    static Task<(string[] Unlocked, string[] Locked)> SearchAsync(DBusConnection connection, string key)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "SearchItems", "a{ss}", MessageFlags.None);
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString(Application);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("key");
        writer.WriteString(key);
        writer.WriteDictionaryEnd(attributes);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            var locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
            return (unlocked, locked);
        }, null);
    }

    static Task<string?> GetSecretAsync(DBusConnection connection, string item, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ServicePath, "org.freedesktop.Secret.Service", "GetSecrets", "aoo", MessageFlags.None);
        writer.WriteArray(new[] { new ObjectPath(item) });
        writer.WriteObjectPath(session);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var end = reader.ReadDictionaryStart();
            while (reader.HasNext(end))
            {
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();
                var value = reader.ReadArrayOfByte();
                reader.ReadString();
                return (string?)Encoding.UTF8.GetString(value);
            }
            return null;
        }, null);
    }
}
