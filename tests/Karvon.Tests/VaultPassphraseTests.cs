using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bengine.Introspection.Servers;
using Karvon.Claude.Tools;
using Karvon.Services;
using Karvon.Workspace;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Karvon's vault with a passphrase: its key in no keyring, the vault locked until Karvon is given it as it starts.</summary>
public class VaultPassphraseTests
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

    sealed class Dialogs : IDialogService
    {
        public Queue<string?> Prompts { get; } = new();

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public List<string> Labels { get; } = [];

        public Task<string?> PromptAsync(string title, string label, string initial)
        {
            Labels.Add(label);
            return Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);
        }

        public Queue<string?> Passphrases { get; } = new();

        public Task<string?> VaultPassphraseAsync(bool change) => Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message) => Task.FromResult<bool?>(true);

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static string NewVault() => Path.Combine(Path.GetTempPath(), "karvon-vault-" + Guid.NewGuid().ToString("N"), "vault.db");

    static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void A_passphrase_keeps_the_key_out_of_the_keyring_and_the_vault_locked_until_it_is_given()
    {
        var keyring = new MemoryKeyring();
        var path = NewVault();
        var vault = SecretVault.Open(keyring, path)!;
        Assert.False(vault.IsLocked);
        Assert.False(vault.HasPassphrase);
        Assert.True(vault.Set("karvon-jira/acme", "Jira", "jira-secret-1"));
        vault.SetPassphrase("correct horse battery");
        Assert.False(keyring.Entries.ContainsKey(SecretVault.KeyName));
        // Open still for this session; the passphrase never reaches Claude.
        Assert.Equal("jira-secret-1", vault.Get("karvon-jira/acme"));
        Assert.Equal("«hidden: karvon-vault/passphrase»", Redaction.Secrets("correct horse battery"));

        // Karvon started again: locked until the passphrase, nothing read or kept.
        var again = SecretVault.Open(keyring, path)!;
        Assert.True(again.IsLocked);
        Assert.True(again.HasPassphrase);
        Assert.Null(again.Get("karvon-jira/acme"));
        Assert.False(again.Set("karvon-jira/other", "Jira", "jira-secret-2"));
        Assert.False(again.Unlock("wrong horse battery"));
        Assert.True(again.IsLocked);
        Assert.True(again.Unlock("correct horse battery"));
        Assert.Equal("jira-secret-1", again.Get("karvon-jira/acme"));

        // Taken away: the key back in the keyring, the vault open as Karvon starts.
        Assert.True(again.RemovePassphrase());
        Assert.True(keyring.Entries.ContainsKey(SecretVault.KeyName));
        var open = SecretVault.Open(keyring, path)!;
        Assert.False(open.IsLocked);
        Assert.Equal("jira-secret-1", open.Get("karvon-jira/acme"));
    }

    [Fact]
    public void A_vault_with_a_passphrase_needs_no_keyring()
    {
        var keyring = new MemoryKeyring();
        var path = NewVault();
        var vault = SecretVault.Open(keyring, path)!;
        vault.Set("karvon-jira/acme", "Jira", "jira-secret-3");
        vault.SetPassphrase("no keyring here");
        var alone = SecretVault.Open(new MemoryKeyring(), path)!;
        Assert.True(alone.Unlock("no keyring here"));
        Assert.Equal("jira-secret-3", alone.Get("karvon-jira/acme"));
    }

    [Fact]
    public void A_forgotten_passphrase_takes_what_the_vault_kept()
    {
        var keyring = new MemoryKeyring();
        var path = NewVault();
        var vault = SecretVault.Open(keyring, path)!;
        vault.Set("karvon-ports/tulki.uz", "Tunnel", "tunnel-secret-1");
        vault.SetPassphrase("long passphrase 1");
        var locked = SecretVault.Open(keyring, path)!;
        Assert.True(locked.Forget());
        Assert.False(locked.IsLocked);
        Assert.False(locked.HasPassphrase);
        Assert.Null(locked.Get("karvon-ports/tulki.uz"));
        Assert.True(keyring.Entries.ContainsKey(SecretVault.KeyName));
        Assert.False(SecretVault.Open(keyring, path)!.IsLocked);
    }

    [AvaloniaFact]
    public async Task Karvon_asks_for_it_first_and_goes_on_locked_when_not_now()
    {
        var keyring = new MemoryKeyring();
        var path = NewVault();
        SecretVault.Open(keyring, path)!.SetPassphrase("open sesame 42");
        var locked = SecretVault.Open(keyring, path)!;
        var window = new VaultUnlockWindow(locked);
        var finished = 0;
        window.Finished += () => finished++;
        window.Show();

        // Not the vault's: said so, and asked again.
        Find<TextBox>(window, "Passphrase").Text = "nope nope";
        Click(Find<Button>(window, "Unlock"));
        await WaitUntil(() => Find<TextBlock>(window, "Error").IsVisible, TimeSpan.FromSeconds(10));
        Assert.Equal("That is not the vault's passphrase.", Find<TextBlock>(window, "Error").Text);
        Assert.Equal(0, finished);
        Snapshot(window, "vault-unlock");
        Find<TextBox>(window, "Passphrase").Text = "open sesame 42";
        Click(Find<Button>(window, "Unlock"));
        await WaitUntil(() => finished == 1, TimeSpan.FromSeconds(10));
        Assert.True(window.Unlocked);
        window.Close();
        Assert.Equal(1, finished);

        // Not now: Karvon goes on, the vault locked.
        var other = SecretVault.Open(keyring, path)!;
        var notNow = new VaultUnlockWindow(other);
        var went = false;
        notNow.Finished += () => went = true;
        notNow.Show();
        Click(Find<Button>(notNow, "Later"));
        Assert.True(went);
        Assert.True(other.IsLocked);
        notNow.Close();
    }

    [AvaloniaFact]
    public async Task Its_window_sets_a_passphrase_long_enough_and_typed_the_same_twice_saying_what_forgetting_it_costs()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var window = new VaultPassphraseWindow(change: false);
        var result = window.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();
        var first = Find<TextBox>(window, "Passphrase");
        var again = Find<TextBox>(window, "Again");
        var set = Find<Button>(window, "Set");
        Assert.False(set.IsEnabled);
        Assert.Contains("cannot be recovered", ((TextBlock)Find<Border>(window, "Forgotten").Child!).Text);

        // Too short: said as it is typed, and Set stays off.
        first.Text = "secret";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("8 characters at least: 6 so far", Find<TextBlock>(window, "Length").Text);
        Assert.False(set.IsEnabled);
        // Long enough, not the same twice.
        first.Text = "long enough 1";
        again.Text = "long enough 2";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("✓ 8 characters at least", Find<TextBlock>(window, "Length").Text);
        Assert.Equal("The two are not the same", Find<TextBlock>(window, "Same").Text);
        Assert.False(set.IsEnabled);
        again.Text = "long enough 1";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("✓ The two are the same", Find<TextBlock>(window, "Same").Text);
        Assert.True(set.IsEnabled);

        // Its eye shows both as typed, and hides them again.
        Assert.Equal('•', first.PasswordChar);
        Click(Find<Button>(window, "Show"));
        Assert.Equal(('\0', '\0'), (first.PasswordChar, again.PasswordChar));
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "vault-passphrase");
        Click(Find<Button>(window, "Show"));
        Assert.Equal(('•', '•'), (first.PasswordChar, again.PasswordChar));

        Click(set);
        Assert.Equal("long enough 1", await result);
        Assert.Null(VaultPassphraseWindow.Problem("long enough 1", "long enough 1"));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Tools_Vault_sets_unlocks_and_removes_it()
    {
        var keyring = new MemoryKeyring();
        var path = NewVault();
        var current = SecretVault.Open(keyring, path)!;
        var vm = NewViewModel();
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        vm.Vault = () => current;
        Assert.True(vm.CanSetVaultPassphrase);

        // Cancelled: not set. Its window (below) gives it only long enough and typed the same twice.
        await vm.SetVaultPassphraseCommand.ExecuteAsync(null);
        Assert.False(current.HasPassphrase);
        dialogs.Passphrases.Enqueue("long enough 1");
        await vm.SetVaultPassphraseCommand.ExecuteAsync(null);
        Assert.True(current.HasPassphrase);
        Assert.True(vm.HasVaultPassphrase);
        Assert.False(vm.CanSetVaultPassphrase);

        // Left locked as Karvon started: unlocked from the menu.
        current = SecretVault.Open(keyring, path)!;
        Assert.True(vm.IsVaultLocked);
        dialogs.Prompts.Enqueue("wrong passphrase");
        dialogs.Prompts.Enqueue("long enough 1");
        await vm.UnlockVaultCommand.ExecuteAsync(null);
        Assert.StartsWith("That is not the vault's passphrase.", dialogs.Labels[^1]);
        Assert.False(current.IsLocked);
        Assert.False(vm.IsVaultLocked);

        // Taken away: the keyring keeps the key again.
        await vm.RemoveVaultPassphraseCommand.ExecuteAsync(null);
        Assert.False(current.HasPassphrase);
        Assert.True(keyring.Entries.ContainsKey(SecretVault.KeyName));
    }
}
