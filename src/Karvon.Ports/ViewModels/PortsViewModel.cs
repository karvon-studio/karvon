using System.Collections.ObjectModel;
using System.Net.Sockets;
using Avalonia.Threading;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ports.Jprq;
using Karvon.Ui;

namespace Karvon.Ports.ViewModels;

/// <summary>What the Ports window needs of Karvon.</summary>
public interface IPortsHost
{
    /// <summary>Karvon's settings folder (ports.json: the tunnel service, without its key).</summary>
    string ConfigDirectory { get; }

    /// <summary>Where the program Karvon runs listens (its output says so).</summary>
    IReadOnlyList<string> ListeningUrls { get; }

    event Action? ListeningChanged;

    void OpenUrl(string url);

    void Status(string text);
}

public enum PortState
{
    Stopped,
    Connecting,
    Online,
    Reconnecting,
    Failed,
}

/// <summary>A port of the running program the window offers to forward (never forwarded by itself: that makes it public).</summary>
public sealed record DetectedPort(int Port, string Url)
{
    /// <summary>An HTTPS port's TLS goes through a TCP tunnel as it is; a plain one is a web address.</summary>
    public string Protocol => Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "tcp" : "http";
}

/// <summary>A forwarded port kept with the solution, to forward again with a click.</summary>
public sealed record SavedForward(int Port, string Protocol, string Subdomain, ushort PublicPort);

/// <summary>
/// A local port at a public address: HTTP at https://name.domain (the service's TLS), TCP at host:port. Started, it stays
/// up: a lost connection is made again (waiting longer each time) until it is stopped or the service refuses it.
/// </summary>
public sealed partial class PortForward(PortsViewModel ports, int port, string protocol, string subdomain, ushort publicPort) : ObservableObject
{
    public int Port => port;

    public string Protocol => protocol;

    public string Subdomain => subdomain;

    public ushort PublicPort => publicPort;

    public string LocalAddress => $"localhost:{port}";

    public string ProtocolText => protocol == "tcp" ? "TCP" : "HTTP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsOnline), nameof(IsConnecting), nameof(IsFailed), nameof(CanOpen), nameof(ConnectionsText))]
    public partial PortState State { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddress), nameof(CanOpen), nameof(AddressText))]
    public partial string? Address { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionsText))]
    public partial int Connections { get; internal set; }

    public bool HasAddress => Address is not null;

    public bool HasError => Error is not null;

    /// <summary>Its public address, or what it will be.</summary>
    public string AddressText => Address ?? (protocol == "tcp" ? "a host and port" : subdomain.Length > 0 ? subdomain + "." + ports.Domain : "your key's name." + ports.Domain);

    public string StateText => State switch
    {
        PortState.Connecting => "Connecting…",
        PortState.Online => "Online",
        PortState.Reconnecting => "Reconnecting…",
        PortState.Failed => "Failed",
        _ => "Stopped",
    };

    /// <summary>Started (it may be connecting): Stop shows; else Start.</summary>
    public bool IsRunning => State is PortState.Connecting or PortState.Online or PortState.Reconnecting;

    public bool IsOnline => State == PortState.Online;

    public bool IsConnecting => State is PortState.Connecting or PortState.Reconnecting;

    public bool IsFailed => State == PortState.Failed;

    public bool CanOpen => IsOnline && protocol == "http" && Address is not null;

    public string ConnectionsText => IsOnline ? Connections.ToString() : "";

    /// <summary>The tunnel while it is open.</summary>
    internal JprqTunnel? Tunnel { get; set; }

    /// <summary>Started: cancelled when it is stopped (its connecting, its reconnecting).</summary>
    internal CancellationTokenSource? Run { get; set; }

    [RelayCommand]
    Task StartAsync() => ports.StartAsync(this);

    [RelayCommand]
    Task StopAsync() => ports.StopAsync(this);

    [RelayCommand]
    void Open()
    {
        if (CanOpen)
            ports.Host.OpenUrl(Address!);
    }

    [RelayCommand]
    Task RemoveAsync() => ports.RemoveAsync(this);
}

/// <summary>
/// The Ports window, as Visual Studio Code's: the forwarded ports (their public address, protocol, visitors, state), the
/// running program's ports to forward, and the tunnel service with its API key (in the system keyring, never Claude's).
/// </summary>
public sealed partial class PortsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a lost tunnel waits before each try to open it again (the last again and again).</summary>
    internal static TimeSpan[] Retries { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>How many tries a "busy" name gets after a lost tunnel (the service holds the old one until it notices).</summary>
    const int BusyTries = 12;

    static readonly string Version = "karvon-" + (typeof(PortsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0");

    readonly Func<string, CancellationToken, Task<JprqEndpoint>> _resolve;
    (string Service, JprqEndpoint Endpoint)? _endpoint;

    /// <summary>The key's own name, the service's default (learnt from a tunnel opened without one).</summary>
    string? _login;

    public PortsViewModel(IPortsHost host, ICredentialStore? keyring, Func<string, CancellationToken, Task<JprqEndpoint>>? resolve = null)
    {
        Host = host;
        Settings = new PortsSettings(Path.Combine(host.ConfigDirectory, "ports.json"));
        Secrets = new PortsSecrets(keyring);
        _resolve = resolve ?? JprqEndpoint.ResolveAsync;
        Service = Settings.Service;
        host.ListeningChanged += OnListeningChanged;
        Forwards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            RefreshDetected();
        };
        RefreshDetected();
    }

    public IPortsHost Host { get; }

    public PortsSettings Settings { get; }

    public PortsSecrets Secrets { get; }

    public ObservableCollection<PortForward> Forwards { get; } = [];

    public ObservableCollection<DetectedPort> Detected { get; } = [];

    public bool IsEmpty => Forwards.Count == 0;

    public bool HasDetected => Detected.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceName), nameof(HasKey), nameof(ServiceText), nameof(Domain), nameof(EmptyText))]
    public partial string Service { get; private set; }

    public string ServiceName => PortsSettings.NameOf(Service);

    /// <summary>The domain HTTP tunnels are under (the service's, once read).</summary>
    public string Domain => _endpoint is { } e && e.Service == Service ? e.Endpoint.Domain : ServiceName;

    public bool HasKey => Secrets.Get(Service) is not null;

    public string ServiceText => HasKey ? $"Through {ServiceName}" : $"Through {ServiceName}: no API key yet";

    public string EmptyText =>
        $"Forward a local port to reach it from the internet: a web app at https://name.{Domain}, or SSH or a database at a host "
        + $"and port, through {ServiceName}. Its API key stays in the system keyring: Claude Code forwards ports through Karvon and never sees it.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>A dialog over the window, one at a time: it, when confirmed; else null.</summary>
    public async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    public PortForward? Find(int port) => Forwards.FirstOrDefault(f => f.Port == port);

    void OnListeningChanged() => Dispatcher.UIThread.Post(RefreshDetected);

    /// <summary>The running program's ports, those not forwarded.</summary>
    void RefreshDetected()
    {
        var forwarded = Forwards.Select(f => f.Port).ToHashSet();
        var detected = Host.ListeningUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0 ? new DetectedPort(uri.Port, url) : null)
            .OfType<DetectedPort>()
            .Where(d => !forwarded.Contains(d.Port))
            .DistinctBy(d => d.Port)
            .ToList();
        if (detected.SequenceEqual(Detected))
            return;
        Detected.Clear();
        foreach (var port in detected)
            Detected.Add(port);
        OnPropertyChanged(nameof(HasDetected));
    }

    /// <summary>Forward a Port: the port (a detected one's given), how, under which name.</summary>
    [RelayCommand]
    async Task ForwardPortAsync(DetectedPort? detected)
    {
        if (await ShowAsync(new ForwardPortDialog(detected, Domain)) is not { } form)
            return;
        await ForwardAsync(form.Port, form.Protocol, form.Subdomain.Trim().ToLowerInvariant(), form.PublicPort, ask: true);
    }

    /// <summary>The tunnel service and its API key.</summary>
    [RelayCommand]
    async Task ServiceSettingsAsync() => await AskServiceAsync();

    async Task<bool> AskServiceAsync()
    {
        var dialog = new PortsServiceDialog(Service, HasKey, Secrets.HasKeyring);
        dialog.OpenSite += Host.OpenUrl;
        if (await ShowAsync(dialog) is not { } form)
            return false;
        var service = form.Service.Trim();
        if (service != Service)
        {
            Settings.Service = service;
            _endpoint = null;
        }
        if (form.Forget)
            Secrets.Forget(service);
        else if (form.Key.Trim() is { Length: > 0 } key)
            Secrets.Set(service, key, form.Keep);
        Service = service;
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(ServiceText));
        return true;
    }

    /// <summary>
    /// A port forwarded (the one already there taken again, or made anew when it changed), started: it, online or with
    /// its error. Without a key, <paramref name="ask"/> asks the user for it; Claude's call does not.
    /// </summary>
    public async Task<PortForward> ForwardAsync(int port, string protocol, string subdomain, ushort publicPort, bool ask)
    {
        var forward = Forwards.FirstOrDefault(f => f.Port == port);
        if (forward is not null && (forward.Protocol != protocol || forward.Subdomain != subdomain || forward.PublicPort != publicPort))
        {
            await StopAsync(forward);
            Forwards.Remove(forward);
            forward = null;
        }
        if (forward is null)
            Forwards.Add(forward = new PortForward(this, port, protocol, subdomain, publicPort));
        await StartAsync(forward, ask);
        return forward;
    }

    public Task StartAsync(PortForward forward) => StartAsync(forward, ask: true);

    async Task StartAsync(PortForward forward, bool ask)
    {
        if (forward.IsRunning)
            return;
        var run = forward.Run = new CancellationTokenSource();
        forward.Error = null;
        forward.State = PortState.Connecting;
        try
        {
            await OpenAsync(forward, await KeyAsync(ask), run.Token);
            Host.Status($"Ports: localhost:{forward.Port} is at {forward.Address}.");
        }
        catch (OperationCanceledException)
        {
            // Stopped meanwhile, or no key given.
            if (forward.Run == run)
            {
                forward.Run = null;
                forward.State = PortState.Stopped;
            }
        }
        catch (Exception ex) when (ex is JprqException or IOException or SocketException or InvalidOperationException)
        {
            if (forward.Run == run)
            {
                forward.Run = null;
                forward.State = PortState.Failed;
                forward.Error = ex.Message;
            }
        }
    }

    /// <summary>The service's API key: kept, or asked (the service dialog), or none (an error saying where it is set).</summary>
    async Task<string> KeyAsync(bool ask)
    {
        if (Secrets.Get(Service) is { } key)
            return key;
        if (!ask)
            throw new InvalidOperationException($"Ports has no API key for {ServiceName}: the user sets it in View > Ports (the service button).");
        if (!await AskServiceAsync() || Secrets.Get(Service) is not { } given)
            throw new OperationCanceledException();
        return given;
    }

    async Task<JprqEndpoint> EndpointAsync(CancellationToken ct)
    {
        if (_endpoint is { } known && known.Service == Service)
            return known.Endpoint;
        var service = Service;
        var endpoint = await _resolve(service, ct);
        _endpoint = (service, endpoint);
        OnPropertyChanged(nameof(Domain));
        OnPropertyChanged(nameof(EmptyText));
        return endpoint;
    }

    async Task OpenAsync(PortForward forward, string key, CancellationToken ct)
    {
        JprqTunnel tunnel;
        var endpoint = await EndpointAsync(ct);
        // A tunnel of either protocol is named (TCP's too: name.domain:port), by the key's name when none is given: one of
        // ours has that name already, so this one is the name and its port.
        var subdomain = forward.Subdomain;
        if (subdomain.Length == 0 && _login is { } login && Forwards.Any(f => f != forward && f.Tunnel?.Opened.Hostname == $"{login}.{endpoint.Domain}"))
            subdomain = $"{login[..Math.Min(login.Length, 32)]}-{forward.Port}";
        try
        {
            tunnel = await JprqTunnel.OpenAsync(endpoint.Host, endpoint.Port,
                new TunnelRequest(forward.Protocol, subdomain, key, Version, forward.PublicPort), forward.Port, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // The service may have moved: its config page is read again next time.
            _endpoint = null;
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            await tunnel.DisposeAsync();
            throw new OperationCanceledException(ct);
        }
        if (subdomain.Length == 0 && tunnel.Opened.Hostname.EndsWith("." + endpoint.Domain, StringComparison.OrdinalIgnoreCase))
            _login = tunnel.Opened.Hostname[..^(endpoint.Domain.Length + 1)];
        forward.Tunnel = tunnel;
        forward.Address = tunnel.PublicAddress;
        forward.Connections = 0;
        forward.Error = null;
        forward.State = PortState.Online;
        tunnel.ConnectionsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (forward.Tunnel == tunnel)
                forward.Connections = tunnel.Connections;
        });
        tunnel.LimitReached += _ => Dispatcher.UIThread.Post(() =>
            Host.Status($"Ports: {tunnel.PublicAddress} turned a visitor away: it has as many connections as {ServiceName} allows."));
        tunnel.Closed += reason => Dispatcher.UIThread.Post(() => _ = LostAsync(forward, tunnel, reason));
    }

    /// <summary>The tunnel's connection was lost: opened again, waiting longer each time, until it is stopped or refused.</summary>
    async Task LostAsync(PortForward forward, JprqTunnel tunnel, string? reason)
    {
        if (forward.Tunnel != tunnel || reason is null)
            return;
        forward.Tunnel = null;
        if (forward.Run is not { IsCancellationRequested: false } run)
            return;
        forward.State = PortState.Reconnecting;
        forward.Error = reason;
        forward.Connections = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await Task.Delay(Retries[Math.Min(attempt, Retries.Length - 1)], run.Token);
                await OpenAsync(forward, await KeyAsync(ask: false), run.Token);
                Host.Status($"Ports: localhost:{forward.Port} is at {forward.Address} again.");
                return;
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is JprqException && (!ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) || attempt >= BusyTries)
                || ex is InvalidOperationException)
            {
                if (forward.Run != run)
                    return;
                forward.Run = null;
                forward.State = PortState.Failed;
                forward.Error = ex.Message;
                return;
            }
            catch (Exception ex) when (ex is JprqException or IOException or SocketException)
            {
                forward.Error = ex.Message;
            }
        }
    }

    public async Task StopAsync(PortForward forward)
    {
        var run = forward.Run;
        forward.Run = null;
        run?.Cancel();
        var tunnel = forward.Tunnel;
        forward.Tunnel = null;
        forward.Connections = 0;
        forward.Error = null;
        forward.State = PortState.Stopped;
        if (tunnel is not null)
            await tunnel.DisposeAsync();
    }

    public async Task RemoveAsync(PortForward forward)
    {
        await StopAsync(forward);
        Forwards.Remove(forward);
    }

    /// <summary>What the solution keeps: the forwarded ports, to forward again with a click (never by themselves).</summary>
    public IReadOnlyList<SavedForward> Saved => [.. Forwards.Select(f => new SavedForward(f.Port, f.Protocol, f.Subdomain, f.PublicPort))];

    /// <summary>The solution's forwarded ports back, stopped; those of another solution stopped and gone.</summary>
    public async Task RestoreAsync(IEnumerable<SavedForward> saved)
    {
        await StopAllAsync();
        Forwards.Clear();
        foreach (var f in saved.DistinctBy(f => f.Port))
            Forwards.Add(new PortForward(this, f.Port, f.Protocol, f.Subdomain, f.PublicPort));
    }

    public async Task StopAllAsync()
    {
        foreach (var forward in Forwards.ToList())
            await StopAsync(forward);
    }

    public void Dispose()
    {
        Host.ListeningChanged -= OnListeningChanged;
        foreach (var forward in Forwards)
        {
            forward.Run?.Cancel();
            if (forward.Tunnel is { } tunnel)
                _ = tunnel.DisposeAsync().AsTask();
        }
    }
}
