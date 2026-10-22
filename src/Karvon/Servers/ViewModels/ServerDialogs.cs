using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Ui;

namespace Karvon.Servers.ViewModels;

/// <summary>A password or a passphrase, typed hidden, and whether to keep it in the system keyring.</summary>
public sealed partial class SecretDialog(string title, string question, bool canSave) : OverlayDialog(title, "Connect")
{
    public string Question { get; } = question;

    /// <summary>A system keyring is there to keep it in.</summary>
    public bool CanSave { get; } = canSave;

    [ObservableProperty]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    public partial bool Save { get; set; }
}

/// <summary>A host key Karvon has not seen: its fingerprint, to compare with the machine's before trusting it.</summary>
public sealed class TrustDialog(string host, string endpoint, string keyType, string fingerprint) : OverlayDialog($"Trust {host}?", "Trust and connect")
{
    public string Message { get; } =
        $"Karvon has not connected to {endpoint} before, and ~/.ssh/known_hosts does not have its key. If you can, compare its fingerprint "
        + "with the machine's own (ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub there).";

    public string KeyType { get; } = keyType;

    public string Fingerprint { get; } = fingerprint;
}

/// <summary>Add Server / Edit: where the machine is, who signs in, its key, its group and whether it is production.</summary>
public sealed partial class ServerFormDialog : OverlayDialog
{
    public ServerFormDialog(ServerHost? existing, IReadOnlyList<string> groups) : base(existing is null ? "Add a server" : $"Edit {existing.Name}", existing is null ? "Add" : "Save")
    {
        Groups = groups;
        IsNew = existing is null;
        IsFromSshConfig = existing?.FromSshConfig == true;
        if (existing is null)
            return;
        Name = existing.Name;
        Address = existing.Address;
        Port = existing.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        User = existing.User;
        IdentityFile = existing.IdentityFiles.FirstOrDefault() ?? "";
        Group = existing.Group ?? "";
        Production = existing.Production;
    }

    public bool IsNew { get; }

    /// <summary>A ~/.ssh/config host: only its group and production flag are Karvon's to change.</summary>
    public bool IsFromSshConfig { get; }

    public bool CanEditConnection => !IsFromSshConfig;

    public IReadOnlyList<string> Groups { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Port { get; set; } = "22";

    [ObservableProperty]
    public partial string User { get; set; } = Environment.UserName;

    /// <summary>A private key; empty for ~/.ssh/id_* (and a password when they do not get in).</summary>
    [ObservableProperty]
    public partial string IdentityFile { get; set; } = "";

    [ObservableProperty]
    public partial string Group { get; set; } = "";

    [ObservableProperty]
    public partial bool Production { get; set; }

    /// <summary>What is wrong with the form, shown under it; null when it can be saved.</summary>
    public string? Problem => Name.Trim().Length == 0 ? "Give it a name."
        : Name.Trim().Any(char.IsWhiteSpace) ? "The name has no spaces (it is used as an alias, as in ~/.ssh/config)."
        : CanEditConnection && Address.Trim().Length == 0 ? "Give the host name or address."
        : CanEditConnection && (!int.TryParse(Port, out var port) || port is < 1 or > 65535) ? "The port is a number from 1 to 65535."
        : CanEditConnection && User.Trim().Length == 0 ? "Give the user to sign in as."
        : null;

    public override bool CanConfirm => Problem is null;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Problem));

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(Problem));

    partial void OnPortChanged(string value) => OnPropertyChanged(nameof(Problem));

    partial void OnUserChanged(string value) => OnPropertyChanged(nameof(Problem));
}
