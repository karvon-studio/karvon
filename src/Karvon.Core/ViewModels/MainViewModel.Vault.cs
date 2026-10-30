using CommunityToolkit.Mvvm.Input;
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

    const int MinPassphrase = 8;

    [RelayCommand]
    async Task SetVaultPassphrase()
    {
        if (Dialogs is null || Vault() is not { IsLocked: false } vault)
            return;
        var first = await Dialogs.PromptAsync("Vault Passphrase",
            "A passphrase for Karvon's vault, asked each time Karvon Studio starts. Forgotten, what the vault keeps is lost.", "", secret: true);
        if (first is not { Length: > 0 })
            return;
        if (first.Length < MinPassphrase)
        {
            SetStatus($"The vault's passphrase was not set: it takes {MinPassphrase} characters at least.");
            return;
        }
        if (await Dialogs.PromptAsync("Vault Passphrase", "The passphrase again", "", secret: true) != first)
        {
            SetStatus("The vault's passphrase was not set: the two were not the same.");
            return;
        }
        await Task.Run(() => vault.SetPassphrase(first));
        SetStatus("Karvon's vault has a passphrase: Karvon Studio asks for it as it starts.");
        VaultChanged();
    }

    [RelayCommand]
    async Task RemoveVaultPassphrase()
    {
        if (Dialogs is null || Vault() is not { IsLocked: false, HasPassphrase: true } vault)
            return;
        if (await Dialogs.ConfirmAsync("Remove the Vault's Passphrase", "The vault's key goes back to the system keyring, which opens it as you sign in to the computer.") != true)
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
        if (await Dialogs.PromptAsync("Unlock Karvon's Vault", "The vault's passphrase", "", secret: true) is not { Length: > 0 } passphrase)
            return;
        SetStatus(await Task.Run(() => vault.Unlock(passphrase))
            ? "Karvon's vault is unlocked: what asks for a sign-in now finds it."
            : "That is not the vault's passphrase.");
        VaultChanged();
    }

    void VaultChanged()
    {
        OnPropertyChanged(nameof(IsVaultLocked));
        OnPropertyChanged(nameof(HasVaultPassphrase));
        OnPropertyChanged(nameof(CanSetVaultPassphrase));
    }
}
