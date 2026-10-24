using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Ports.ViewModels;

/// <summary>
/// Forward a Port: the local port, HTTP (a web address under the service's domain, by a name: the key's own when none is
/// given) or TCP (a host and port, a fixed one when the service allows it).
/// </summary>
public sealed partial class ForwardPortDialog : OverlayDialog
{
    public ForwardPortDialog(DetectedPort? detected, string domain) : base("Forward a Port", "Forward")
    {
        Domain = domain;
        if (detected is not null)
        {
            PortText = detected.Port.ToString();
            ProtocolIndex = detected.Protocol == "tcp" ? 1 : 0;
        }
    }

    public string Domain { get; }

    public static IReadOnlyList<string> Protocols { get; } = ["HTTP: a web address (https://name.domain)", "TCP: a host and port (SSH, a database, HTTPS as it is)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string PortText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm), nameof(IsHttp), nameof(IsTcp), nameof(Preview))]
    public partial int ProtocolIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm), nameof(Preview))]
    public partial string Subdomain { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string PublicPortText { get; set; } = "";

    public bool IsHttp => ProtocolIndex == 0;

    public bool IsTcp => ProtocolIndex == 1;

    public string Protocol => IsTcp ? "tcp" : "http";

    public int Port => int.TryParse(PortText.Trim(), out var port) ? port : 0;

    public ushort PublicPort => IsTcp && ushort.TryParse(PublicPortText.Trim(), out var port) ? port : (ushort)0;

    /// <summary>The address it will have.</summary>
    public string Preview => IsTcp ? $"{Domain}:<port>" : $"https://{(Subdomain.Trim() is { Length: > 0 } name ? name.ToLowerInvariant() : "<your key's name>")}.{Domain}";

    public string? Problem =>
        Port is < 1 or > 65535 ? "The local port: a number from 1 to 65535."
        : IsHttp && Subdomain.Trim() is { Length: > 0 } name && !ValidName(name) ? "The name: 3 to 38 letters, digits and hyphens."
        : IsTcp && PublicPortText.Trim().Length > 0 && PublicPort == 0 ? "The public port: a number (the service says which it allows)."
        : null;

    static bool ValidName(string name) => name.Length is >= 3 and <= 38 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public override bool CanConfirm => Problem is null;
}

/// <summary>
/// The tunnel service: its config page (or event port), and its API key (kept in the system keyring when asked; the
/// service's site says where to get one).
/// </summary>
public sealed partial class PortsServiceDialog(string service, bool hasKey, bool canKeep) : OverlayDialog("Tunnel Service", "Save")
{
    public bool HasKey { get; } = hasKey;

    public bool CanKeep { get; } = canKeep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Site), nameof(HasSite), nameof(Problem), nameof(CanConfirm))]
    public partial string Service { get; set; } = service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string Key { get; set; } = "";

    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    /// <summary>The key is to be forgotten (Forget key).</summary>
    public bool Forget { get; private set; }

    /// <summary>The service's site, which says how to get a key.</summary>
    public string? Site => PortsSettings.SiteOf(Service.Trim());

    public bool HasSite => Site is not null;

    public string KeyPlaceholder => HasKey ? "Saved; type a new one to change it" : "The service's API key";

    public string? Problem =>
        Service.Trim().Length == 0 ? "The service: its config page (https://tulki.uz/config.json) or its event port (host:4321)." : null;

    /// <summary>A key is given, or one is kept already.</summary>
    public override bool CanConfirm => Problem is null && (HasKey || Key.Trim().Length > 0);

    [RelayCommand]
    void ForgetKey()
    {
        Forget = true;
        Key = "";
        Confirm();
    }

    /// <summary>Its site to open in the browser.</summary>
    public event Action<string>? OpenSite;

    [RelayCommand]
    void ShowSite()
    {
        if (Site is { } site)
            OpenSite?.Invoke(site);
    }
}
