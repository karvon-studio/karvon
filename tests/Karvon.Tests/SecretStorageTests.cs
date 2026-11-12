using System.Text;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Karvon.Claude;
using Karvon.Database;
using Karvon.Database.ViewModels;
using Karvon.Services;

namespace Karvon.Tests;

/// <summary>
/// Where Karvon keeps what must stay secret: a data source's password in the vault and never in a list's file, a vault
/// copied to another machine that opens for no one, the agents' conversations their owner's alone.
/// </summary>
public class SecretStorageTests
{
    sealed class MemoryKeyring : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = [];

        public string Name => "memory";

        public string? Get(string key) => Entries.GetValueOrDefault(key);

        public bool Set(string key, string label, string secret)
        {
            Entries[key] = secret;
            return true;
        }

        public void Delete(string key) => Entries.Remove(key);
    }

    static string NewFolder() => Directory.CreateTempSubdirectory("karvon-secrets-").FullName;

    static ServerProfile Orders() => new()
    {
        Id = "orders",
        Name = "Orders",
        Provider = DatabaseProvider.PostgreSql,
        Host = "db.example.test",
        Username = "app",
        Password = "db-pass-1",
        SshHost = "jump.example.test",
        SshPassword = "ssh-pass-1",
        SavePassword = true,
    };

    static VaultOrSession Over(SecretVault vault) => new(vault, () => !vault.IsLocked);

    static SecretVault Locked(MemoryKeyring keyring, string path)
    {
        SecretVault.Open(keyring, path)!.SetPassphrase("a long passphrase of mine");
        var locked = SecretVault.Open(keyring, path)!;
        Assert.True(locked.IsLocked);
        return locked;
    }

    static void NotWritten(string file)
    {
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("db-pass-1", text);
        Assert.DoesNotContain("ssh-pass-1", text);
    }

    [Fact]
    public void A_data_sources_saved_password_is_kept_in_the_vault_never_in_the_list_file()
    {
        var folder = NewFolder();
        var keyring = new MemoryKeyring();
        var vaultFile = Path.Combine(folder, "vault.db");
        var vault = SecretVault.Open(keyring, vaultFile)!;
        var file = Path.Combine(folder, "servers.json");

        new ServerStore(file, Over(vault)).Upsert(Orders());
        NotWritten(file);
        Assert.Equal(("db-pass-1", "ssh-pass-1"), (vault.Get("orders"), vault.Get("orders:ssh")));

        // Karvon started again: found in the vault.
        var again = new ServerStore(file, Over(SecretVault.Open(keyring, vaultFile)!)).Get("orders")!;
        Assert.Equal(("db-pass-1", "ssh-pass-1"), (again.Password, again.SshPassword));
    }

    [Fact]
    public void With_the_vault_locked_or_none_a_saved_password_lasts_until_Karvon_closes_and_is_never_written_down()
    {
        var folder = NewFolder();
        var keyring = new MemoryKeyring();
        var vaultFile = Path.Combine(folder, "vault.db");
        var locked = Locked(keyring, vaultFile);
        var file = Path.Combine(folder, "servers.json");
        var store = new ServerStore(file, Over(locked));
        Assert.Equal("Karvon's vault is locked (or there is no keyring): a saved password is kept until Karvon closes.",
            new ServerFormViewModel(store, null).PasswordStorage);

        store.Upsert(Orders());
        NotWritten(file);
        Assert.Equal("db-pass-1", store.Get("orders")!.Password);
        // Karvon started again: asked again.
        Assert.Null(new ServerStore(file, Over(SecretVault.Open(keyring, vaultFile)!)).Get("orders")!.Password);

        // No vault and no keyring at all: the same.
        var alone = Path.Combine(folder, "alone.json");
        var none = new ServerStore(alone, new VaultOrSession(null));
        none.Upsert(Orders());
        NotWritten(alone);
        Assert.Equal("ssh-pass-1", none.Get("orders")!.SshPassword);
    }

    [Fact]
    public void A_password_an_older_Karvon_left_in_the_list_file_is_moved_into_the_vault()
    {
        var folder = NewFolder();
        var keyring = new MemoryKeyring();
        var file = Path.Combine(folder, "servers.json");
        new ServerStore(file, credentials: null).Upsert(Orders());
        Assert.Contains("db-pass-1", File.ReadAllText(file));

        // The vault locked: left where it is (nothing could keep it now).
        DatabaseModule.MovePasswordsOut(new ServerStore(file, Over(Locked(keyring, Path.Combine(folder, "locked.db")))));
        Assert.Contains("db-pass-1", File.ReadAllText(file));

        var vault = SecretVault.Open(keyring, Path.Combine(folder, "vault.db"))!;
        var store = DatabaseModule.MovePasswordsOut(new ServerStore(file, Over(vault)));
        NotWritten(file);
        Assert.Equal(("db-pass-1", "ssh-pass-1"), (vault.Get("orders"), vault.Get("orders:ssh")));
        Assert.Equal("db-pass-1", store.Get("orders")!.Password);
    }

    [Fact]
    public void A_vault_copied_to_another_machine_opens_for_no_one_without_this_keyring_or_its_passphrase()
    {
        var keyring = new MemoryKeyring();
        var path = Path.Combine(NewFolder(), "vault.db");
        var vault = SecretVault.Open(keyring, path)!;
        vault.Set("karvon-jira/acme", "Karvon: Jira Acme", "jira-token-77");
        vault.Set("karvon-browser/https://shop.example/nbk", "Karvon browser: shop.example (nbk)", "browser-pass-77");

        // The file holds neither a secret nor the key that opens them.
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("jira-token-77", text);
        Assert.DoesNotContain("browser-pass-77", text);
        var key = keyring.Entries[SecretVault.KeyName];
        Assert.DoesNotContain(key, text);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Convert.FromBase64String(key)));

        // Copied to another machine, whose keyring has not this one's key: nothing in it reads.
        var copy = Path.Combine(NewFolder(), "vault.db");
        File.Copy(path, copy);
        var elsewhere = SecretVault.Open(new MemoryKeyring(), copy)!;
        Assert.Null(elsewhere.Get("karvon-jira/acme"));
        Assert.Null(elsewhere.Get("karvon-browser/https://shop.example/nbk"));

        // With a passphrase it opens elsewhere for its passphrase only.
        vault.SetPassphrase("a long passphrase of mine");
        var stolen = Path.Combine(NewFolder(), "vault.db");
        File.Copy(path, stolen);
        var taken = SecretVault.Open(new MemoryKeyring(), stolen)!;
        Assert.True(taken.IsLocked);
        Assert.False(taken.Unlock("a guess"));
        Assert.Null(taken.Get("karvon-jira/acme"));
        Assert.True(taken.Unlock("a long passphrase of mine"));
        Assert.Equal("jira-token-77", taken.Get("karvon-jira/acme"));
    }

    [Fact]
    public void An_agents_conversation_is_readable_by_its_owner_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix file modes.");
        const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode folder = file | UnixFileMode.UserExecute;
        const UnixFileMode shared = folder | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var agents = Path.Combine(NewFolder(), "agents");
        var work = Path.Combine(Path.GetTempPath(), "karvon-work");
        // What an older Karvon left: its folders and a transcript readable by others.
        var conversations = AgentTranscripts.Folder(agents, work);
        Directory.CreateDirectory(conversations);
        File.SetUnixFileMode(agents, shared);
        File.SetUnixFileMode(conversations, shared);
        var older = Path.Combine(conversations, AgentTranscripts.FileName("s0"));
        File.WriteAllText(older, "{\"kind\":\"start\",\"agent\":\"codex\",\"session\":\"s0\"}\n");
        File.SetUnixFileMode(older, file | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        AgentTranscript.Open(agents, "codex", "Codex", work, "s1").Prompt("the token is in .env", null);
        AgentTranscript.Open(agents, "codex", "Codex", work, "s0").Prompt("and again", null);

        Assert.Equal(folder, File.GetUnixFileMode(agents));
        Assert.Equal(folder, File.GetUnixFileMode(conversations));
        Assert.Equal(file, File.GetUnixFileMode(Path.Combine(conversations, AgentTranscripts.FileName("s1"))));
        Assert.Equal(file, File.GetUnixFileMode(older));
        Assert.Contains("and again", File.ReadAllText(older));
    }
}
