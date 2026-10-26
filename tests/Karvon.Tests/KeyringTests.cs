using System.Diagnostics;
using Karvon.Services;
using Tmds.DBus.Protocol;

namespace Karvon.Tests;

/// <summary>
/// Karvon's keyring: its Secret Service store speaks the D-Bus that Karvon runs with (bengine's, built against an older
/// one, did not load beside Avalonia 12, so every token was asked again each session). Never the user's own keyring.
/// </summary>
public class KeyringTests
{
    [Fact]
    public void The_secret_service_store_talks_over_the_dbus_that_Karvon_loads()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/dbus-daemon"), "Linux with dbus-daemon.");
        // A bus of its own with no services on it (none activated: never the user's keyring).
        var dir = Directory.CreateTempSubdirectory("karvon-dbus-").FullName;
        var socket = Path.Combine(dir, "bus");
        var config = Path.Combine(dir, "bus.conf");
        File.WriteAllText(config, $"""
            <busconfig>
              <type>session</type>
              <listen>unix:path={socket}</listen>
              <policy context="default"><allow send_destination="*" eavesdrop="true"/><allow eavesdrop="true"/><allow own="*"/></policy>
            </busconfig>
            """);
        using var daemon = Process.Start(new ProcessStartInfo("/usr/bin/dbus-daemon", ["--config-file", config, "--nofork"]) { RedirectStandardError = true })!;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(socket) && DateTime.UtcNow < until)
                Thread.Sleep(50);

            // The bus answers that no keyring is on it: the store's connection and messages went out.
            var store = new SecretServiceKeyring("unix:path=" + socket);
            var error = Assert.Throws<DBusErrorReplyException>(() => store.Get("karvon-test"));
            Assert.Equal("org.freedesktop.DBus.Error.ServiceUnknown", error.ErrorName);
        }
        finally
        {
            daemon.Kill();
            daemon.WaitForExit();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_tests_keyring_is_off()
    {
        // TestEnvironment's BENGINE_KEYRING=off: no test reaches the user's keyring.
        Assert.Null(Keyring.System);
    }
}
