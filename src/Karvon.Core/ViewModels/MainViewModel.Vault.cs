using CommunityToolkit.Mvvm.Input;
using Karvon.Modules;
using Karvon.Services;

namespace Karvon.ViewModels;

/// <summary>
/// Tools > Vault: a passphrase for Karvon's vault (asked as Karvon Studio starts, the key then in no keyring), changed or
/// taken away, and the vault unlocked when it was left locked as Karvon started.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Karvon's vault: the keyring service's, unless another is given (the tests').</summary>
    public Func<SecretVault?> Vault { get; set; } = () => Keyring.Vault;

    public bool HasVault => Vault() is not null;

    public bool IsVaultLocked => Vault() is { IsLocked: true };

    public bool HasVaultPassphrase => Vault() is { IsLocked: false, HasPassphrase: true };

    public bool CanSetVaultPassphrase => Vault() is { IsLocked: false, HasPassphrase: false };

    [RelayCommand]
    async Task SetVaultPassphrase()
    {
        if (Dialogs is null || Vault() is not { IsLocked: false } vault)
            return;
        // Its window checks it as it is typed (long enough, the same twice), and says what forgetting it costs.
        if (await Dialogs.VaultPassphraseAsync(vault.HasPassphrase) is not { Length: >= SecretVault.MinPassphrase } passphrase)
            return;
        await Task.Run(() => vault.SetPassphrase(passphrase));
        SetStatus("Karvon's vault has a passphrase: Karvon Studio asks for it as it starts.");
        VaultChanged();
    }

    [RelayCommand]
    async Task RemoveVaultPassphrase()
    {
        if (Dialogs is null || Vault() is not { IsLocked: false, HasPassphrase: true } vault)
            return;
        var lost = vault.SealedCount > 0
            ? "\n\nWhat only the passphrase opens goes with it: the values of Postman's environments, its cookies and access tokens."
            : "";
        if (await Dialogs.ConfirmAsync("Remove the Vault's Passphrase", "The vault's key goes back to the system keyring, which opens it as you sign in to the computer." + lost) != true)
            return;
        SetStatus(vault.RemovePassphrase()
            ? "Karvon's vault has no passphrase: the system keyring keeps its key."
            : "The system keyring could not keep the vault's key: the passphrase stays.");
        VaultChanged();
    }

    [RelayCommand]
    async Task UnlockVault()
    {
        if (Dialogs is null || Vault() is not { IsLocked: true } vault)
            return;
        var label = "The vault's passphrase";
        while (true)
        {
            if (await Dialogs.PromptAsync("Unlock Karvon's Vault", label, "", secret: true) is not { Length: > 0 } passphrase)
                return;
            if (await Task.Run(() => vault.Unlock(passphrase)))
                break;
            label = "That is not the vault's passphrase. The vault's passphrase";
        }
        SetStatus("Karvon's vault is unlocked: what asks for a sign-in now finds it.");
        VaultChanged();
    }

    /// <summary>The vault open with a passphrase: unlocked, or one set, as the user is asked; false when they do not.</summary>
    async Task<bool> IWorkbench.OpenSealedAsync()
    {
        if (IsVaultLocked)
            await UnlockVault();
        else if (CanSetVaultPassphrase)
            await SetVaultPassphrase();
        return Vault() is { CanSeal: true };
    }

    void VaultChanged()
    {
        OnPropertyChanged(nameof(IsVaultLocked));
        OnPropertyChanged(nameof(HasVaultPassphrase));
        OnPropertyChanged(nameof(CanSetVaultPassphrase));
    }
}
