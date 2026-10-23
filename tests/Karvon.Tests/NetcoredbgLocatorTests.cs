using System.Security.Cryptography;
using Karvon.Debugging;

namespace Karvon.Tests;

/// <summary>netcoredbg's build of every platform Karvon installs it on: there, as pinned, with its program in it.</summary>
public class NetcoredbgLocatorTests
{
    [Fact]
    public void Linux_macOS_and_Windows_have_a_build()
    {
        Assert.Equal(["linux-arm64", "linux-x64", "osx-arm64", "win-x64"], NetcoredbgLocator.AllBuilds.Keys.Order());
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
            Assert.Equal("linux-x64", NetcoredbgLocator.Platform);
    }

    [Fact]
    public async Task Every_platforms_build_is_as_pinned()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_DOWNLOAD_LIVE") == "1", "Set KARVON_DOWNLOAD_LIVE=1 to download every platform's netcoredbg.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        foreach (var (platform, (file, sha256)) in NetcoredbgLocator.AllBuilds)
        {
            var bytes = await http.GetByteArrayAsync($"https://github.com/Samsung/netcoredbg/releases/download/{NetcoredbgLocator.Version}/{file}", ct);
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var into = Path.Combine(Path.GetTempPath(), "karvon-netcoredbg-" + Guid.NewGuid().ToString("N"));
            await NetcoredbgLocator.ExtractAsync(bytes, file, into, platform.StartsWith("win", StringComparison.Ordinal) ? "netcoredbg.exe" : "netcoredbg", ct);
            if (!platform.StartsWith("win", StringComparison.Ordinal))
                Assert.True(File.GetUnixFileMode(Path.Combine(into, "netcoredbg", "netcoredbg")).HasFlag(UnixFileMode.UserExecute), platform);
            Directory.Delete(into, recursive: true);
        }
    }
}
