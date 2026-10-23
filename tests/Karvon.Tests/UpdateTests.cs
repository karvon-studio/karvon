using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Karvon.Updates;

namespace Karvon.Tests;

/// <summary>Karvon Studio's updates: its releases read, the newest for this install found, downloaded, checked and put in place.</summary>
public class UpdateTests
{
    static string NewFolder() => Directory.CreateTempSubdirectory("karvon-update-").FullName;

    /// <summary>A release in GitHub's format, its files in a folder (file: URLs), with their SHA256SUMS.</summary>
    static object Release(string version, string folder, IEnumerable<string> files, bool prerelease = false, bool draft = false, bool sums = true)
    {
        var assets = files.Select(name => new { name, browser_download_url = new Uri(Path.Combine(folder, name)).AbsoluteUri, size = new FileInfo(Path.Combine(folder, name)).Length }).ToList();
        if (sums)
        {
            File.WriteAllLines(Path.Combine(folder, "SHA256SUMS"),
                files.Select(name => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, name))))}  {name}"));
            assets.Add(new { name = "SHA256SUMS", browser_download_url = new Uri(Path.Combine(folder, "SHA256SUMS")).AbsoluteUri, size = 0L });
        }
        return new { tag_name = "v" + version, name = "Karvon Studio " + version, body = $"## {version}\n\n- Better.", html_url = $"https://example.invalid/{version}", draft, prerelease, published_at = "2026-10-24T10:00:00Z", assets };
    }

    static string Feed(string folder, params object[] releases)
    {
        var feed = Path.Combine(folder, "releases.json");
        File.WriteAllText(feed, JsonSerializer.Serialize(releases));
        return feed;
    }

    /// <summary>An installed Karvon Studio of a version: its program says which, its marker how it was installed.</summary>
    static string App(string folder, string distribution, string says)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, OperatingSystem.IsWindows() ? "Karvon.exe" : "Karvon"), says);
        File.WriteAllText(Path.Combine(folder, AppInstall.MarkerFile), distribution);
        return folder;
    }

    [Fact]
    public async Task The_newest_release_with_this_installs_file_is_its_update()
    {
        var dir = NewFolder();
        var files = Path.Combine(dir, "files");
        Directory.CreateDirectory(files);
        foreach (var name in new[] { "karvon-studio-0.2.0-linux-x64.tar.gz", "karvon-studio-0.3.0-rc.1-linux-x64.tar.gz", "karvon-studio-0.9.0-linux-x64.tar.gz" })
            File.WriteAllText(Path.Combine(files, name), name);
        var feed = Feed(dir,
            Release("0.3.0-rc.1", files, ["karvon-studio-0.3.0-rc.1-linux-x64.tar.gz"], prerelease: true),
            Release("0.9.0", files, ["karvon-studio-0.9.0-linux-x64.tar.gz"], draft: true),
            Release("0.2.1", files, []),
            Release("0.2.0", files, ["karvon-studio-0.2.0-linux-x64.tar.gz"]));

        // GitHub's JSON: drafts left out, each version its tag's.
        var releases = UpdateFeed.Parse(File.ReadAllText(feed));
        Assert.Equal(["0.3.0-rc.1", "0.2.1", "0.2.0"], releases.Select(r => r.Version));
        Assert.Equal(("Karvon Studio 0.2.0", "## 0.2.0\n\n- Better.", "https://example.invalid/0.2.0"), (releases[2].Name, releases[2].Notes, releases[2].PageUrl));

        // A .tar.gz's install: the newest release that has its file, not a prerelease.
        var tar = new Updater(new AppInstall(Distribution.Tar, "0.1.0", Path.Combine(dir, "app")), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        Assert.Equal("0.2.0", (await tar.CheckAsync())?.Version);
        Assert.Equal((UpdateState.Available, "Karvon Studio 0.2.0 is available."), (tar.State, tar.Message));

        // Up to date; a build of the sources says a release is out, and does not update.
        var current = new Updater(new AppInstall(Distribution.Tar, "0.2.0", Path.Combine(dir, "app")), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        await current.CheckAsync();
        Assert.Equal((UpdateState.UpToDate, "Karvon Studio is up to date."), (current.State, current.Message));
        var source = new Updater(new AppInstall(Distribution.Source, "0.1.0", Path.Combine(dir, "app")), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        Assert.Equal("0.2.1", (await source.CheckAsync())?.Version);
        Assert.Equal(UpdateState.UpToDate, source.State);
        Assert.Contains("git pull", source.Message);
        Assert.False(await source.DownloadAsync());

        // No feed to read: it says why.
        var broken = new Updater(new AppInstall(Distribution.Tar, "0.1.0", dir), new UpdateFeed(Path.Combine(dir, "nowhere.json")), dir);
        await broken.CheckAsync();
        Assert.Equal(UpdateState.Failed, broken.State);
        Assert.StartsWith("Could not check for updates: ", broken.Message);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void An_install_says_how_it_updates_by_its_package()
    {
        var dir = NewFolder();
        Assert.Equal(Distribution.Source, AppInstall.Of(dir, "0.1.0").Distribution);
        foreach (var (marker, distribution, asset) in new[]
        {
            ("deb", Distribution.Deb, "karvon-studio_1.2.3_amd64.deb"),
            ("tar", Distribution.Tar, "karvon-studio-1.2.3-linux-x64.tar.gz"),
            ("setup", Distribution.Setup, "Karvon-Studio-1.2.3-Setup.exe"),
            ("zip", Distribution.Zip, "karvon-studio-1.2.3-win-x64.zip"),
        })
        {
            File.WriteAllText(Path.Combine(dir, AppInstall.MarkerFile), marker + "\n");
            var install = AppInstall.Of(dir, "1.2.2");
            Assert.Equal((distribution, asset, true), (install.Distribution, install.AssetName("1.2.3"), install.CanUpdate));
        }
        // The AppImage's marker counts only in its AppImage; a Mac's file is its architecture's.
        File.WriteAllText(Path.Combine(dir, AppInstall.MarkerFile), "appimage");
        Assert.Equal(Distribution.Source, AppInstall.Of(dir, "1.0.0").Distribution);
        var appImage = AppInstall.Of(dir, "1.0.0", "/home/me/Karvon.AppImage");
        Assert.Equal((Distribution.AppImage, "/home/me/Karvon.AppImage", "Karvon-Studio-1.1.0-x86_64.AppImage"), (appImage.Distribution, appImage.Program, appImage.AssetName("1.1.0")));
        File.WriteAllText(Path.Combine(dir, AppInstall.MarkerFile), "dmg");
        Assert.Matches(@"^Karvon-Studio-1\.1\.0-(arm64|x64)\.dmg$", AppInstall.Of(dir, "1.0.0").AssetName("1.1.0"));
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInstall.CurrentVersion);
        Assert.DoesNotContain('+', AppInstall.CurrentVersion);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_tar_installs_folder_is_replaced_by_the_checked_update()
    {
        TestTools.SkipOnWindows("A .tar.gz is Linux's.");
        var dir = NewFolder();
        var app = App(Path.Combine(dir, "apps", "karvon-0.1.0"), "tar", "0.1.0");
        File.WriteAllText(Path.Combine(app, "old-only.dll"), "");

        // The release's .tar.gz: one folder, karvon-0.2.0, its program runnable.
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var next = App(Path.Combine(dir, "build", "karvon-0.2.0"), "tar", "0.2.0");
        File.SetUnixFileMode(Path.Combine(next, "Karvon"), (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(Path.Combine(next, "lib"));
        File.WriteAllText(Path.Combine(next, "lib", "Karvon.Core.dll"), "new");
        var asset = Path.Combine(files, "karvon-studio-0.2.0-linux-x64.tar.gz");
        await using (var gz = new GZipStream(File.Create(asset), CompressionLevel.Fastest))
            await TarFile.CreateFromDirectoryAsync(next, gz, includeBaseDirectory: true, TestContext.Current.CancellationToken);
        var feed = Feed(dir, Release("0.2.0", files, [Path.GetFileName(asset)]));
        var cache = Path.Combine(dir, "cache");

        var updater = new Updater(AppInstall.Of(app, "0.1.0"), new UpdateFeed(feed), cache);
        await updater.CheckAsync();
        Assert.True(await updater.DownloadAsync(), updater.Message);
        Assert.Equal((UpdateState.Ready, 1.0), (updater.State, updater.Progress));
        Assert.Equal("Karvon Studio 0.2.0 is ready: it installs when Karvon Studio restarts.", updater.Message);
        // Unpacked beside the install, which is as it was until it closes.
        Assert.True(File.Exists(Path.Combine(dir, "apps", ".karvon-update-0.2.0", "lib", "Karvon.Core.dll")));
        Assert.Equal("0.1.0", File.ReadAllText(Path.Combine(app, "Karvon")));

        Assert.True(await updater.ApplyAsync(restart: false, Environment.ProcessId), updater.Message);
        Assert.Equal(("0.2.0", "new"), (File.ReadAllText(Path.Combine(app, "Karvon")), File.ReadAllText(Path.Combine(app, "lib", "Karvon.Core.dll"))));
        Assert.True(File.GetUnixFileMode(Path.Combine(app, "Karvon")).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.Exists(Path.Combine(app, "old-only.dll")));
        Assert.Equal(["karvon-0.1.0"], Directory.GetFileSystemEntries(Path.Combine(dir, "apps")).Select(Path.GetFileName));
        Assert.Equal(UpdateState.Idle, updater.State);

        // Installed: its download goes.
        new Updater(AppInstall.Of(app, "0.2.0"), new UpdateFeed(feed), cache).CleanCache();
        Assert.Empty(Directory.GetDirectories(cache));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task An_update_whose_checksum_is_not_the_releases_is_not_kept()
    {
        var dir = NewFolder();
        var app = App(Path.Combine(dir, "app"), "tar", "0.1.0");
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var asset = "karvon-studio-0.2.0-linux-x64.tar.gz";
        File.WriteAllText(Path.Combine(files, asset), "the release's");
        var feed = Feed(dir, Release("0.2.0", files, [asset]));
        File.WriteAllText(Path.Combine(files, asset), "another");

        var updater = new Updater(AppInstall.Of(app, "0.1.0"), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        await updater.CheckAsync();
        Assert.False(await updater.DownloadAsync());
        Assert.Equal(UpdateState.Failed, updater.State);
        Assert.EndsWith($"{asset}'s checksum is not the release's: it was not kept.", updater.Message);
        Assert.False(File.Exists(Path.Combine(dir, "cache", "0.2.0", asset)));
        Assert.False(await updater.ApplyAsync(restart: false, Environment.ProcessId));
        Assert.Equal("0.1.0", File.ReadAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "Karvon.exe" : "Karvon")));

        // A release without its checksums is not taken either.
        Feed(dir, Release("0.2.0", files, [asset], sums: false));
        var withoutSums = new Updater(AppInstall.Of(app, "0.1.0"), new UpdateFeed(Path.Combine(dir, "releases.json")), Path.Combine(dir, "cache"));
        await withoutSums.CheckAsync();
        Assert.False(await withoutSums.DownloadAsync());
        Assert.EndsWith($"the release has no SHA256SUMS to check {asset} with.", withoutSums.Message);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task An_appimage_is_replaced_by_its_update()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "An AppImage is Linux's.");
        var dir = NewFolder();
        var image = Path.Combine(dir, "Karvon-Studio.AppImage");
        File.WriteAllText(image, "0.1.0");
        var mounted = App(Path.Combine(dir, "mount", "usr", "lib", "karvon"), "appimage", "0.1.0");
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        File.WriteAllText(Path.Combine(files, "Karvon-Studio-0.2.0-x86_64.AppImage"), "0.2.0");
        var feed = Feed(dir, Release("0.2.0", files, ["Karvon-Studio-0.2.0-x86_64.AppImage"]));

        var updater = new Updater(AppInstall.Of(mounted, "0.1.0", image), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        await updater.CheckAsync();
        Assert.True(await updater.DownloadAsync(), updater.Message);
        Assert.True(await updater.ApplyAsync(restart: false, Environment.ProcessId), updater.Message);
        Assert.Equal("0.2.0", File.ReadAllText(image));
        Assert.True(File.GetUnixFileMode(image).HasFlag(UnixFileMode.UserExecute));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".update", StringComparison.Ordinal));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Windows_files_are_copied_over_once_karvon_has_exited()
    {
        var dir = NewFolder();
        var app = App(Path.Combine(dir, "Karvon Studio"), "setup", "0.1.0");
        var installer = new Updater(AppInstall.Of(app, "0.1.0"), new UpdateFeed(Path.Combine(dir, "none.json")), dir);
        // The installer runs silently for this user (Program Files' install, for all), then Karvon starts again.
        var script = installer.ScriptFor(@"C:\cache\Karvon-Studio-0.2.0-Setup.exe", restart: true, 4242, ["App.sln"]);
        Assert.Contains("tasklist /fi \"PID eq 4242\"", script);
        Assert.Contains("\"C:\\cache\\Karvon-Studio-0.2.0-Setup.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL /CURRENTUSER\r\n", script);
        Assert.EndsWith($"start \"\" \"{Path.Combine(app, OperatingSystem.IsWindows() ? "Karvon.exe" : "Karvon")}\" \"App.sln\"\r\n", script);
        Assert.DoesNotContain("start", installer.ScriptFor(@"C:\cache\setup.exe", restart: false, 4242, []).Split("\r\n")[^2]);

        // A .zip's install, on Windows: its files copied over the folder once Karvon (here a process that is over) exited.
        if (!OperatingSystem.IsWindows())
        {
            Directory.Delete(dir, recursive: true);
            return;
        }
        File.WriteAllText(Path.Combine(app, AppInstall.MarkerFile), "zip");
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var next = App(Path.Combine(dir, "build"), "zip", "0.2.0");
        var asset = Path.Combine(files, "karvon-studio-0.2.0-win-x64.zip");
        ZipFile.CreateFromDirectory(next, asset);
        var feed = Feed(dir, Release("0.2.0", files, [Path.GetFileName(asset)]));
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        await exited.WaitForExitAsync(TestContext.Current.CancellationToken);

        var updater = new Updater(AppInstall.Of(app, "0.1.0"), new UpdateFeed(feed), Path.Combine(dir, "cache"));
        await updater.CheckAsync();
        Assert.True(await updater.DownloadAsync(), updater.Message);
        Assert.True(await updater.ApplyAsync(restart: false, exited.Id), updater.Message);
        await UiTests.WaitUntil(() => File.ReadAllText(Path.Combine(app, "Karvon.exe")) == "0.2.0", TimeSpan.FromSeconds(60));
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(app, "Karvon.exe")));
        await TestTools.DeleteFolderAsync(dir);
    }

    [Fact]
    public async Task A_macs_app_is_replaced_by_the_dmgs()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "A .dmg is a Mac's.");
        var dir = NewFolder();
        var app = App(Path.Combine(dir, "Applications", "Karvon Studio.app", "Contents", "MacOS"), "dmg", "0.1.0");
        var install = AppInstall.Of(app, "0.1.0");
        var dmgFolder = Path.Combine(dir, "dmg");
        App(Path.Combine(dmgFolder, "Karvon Studio.app", "Contents", "MacOS"), "dmg", "0.2.0");
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var asset = Path.Combine(files, install.AssetName("0.2.0")!);
        using (var hdiutil = Process.Start("hdiutil", ["create", "-volname", "Karvon Studio", "-srcfolder", dmgFolder, "-ov", "-format", "UDZO", asset])!)
            await hdiutil.WaitForExitAsync(TestContext.Current.CancellationToken);
        var feed = Feed(dir, Release("0.2.0", files, [Path.GetFileName(asset)]));

        var updater = new Updater(install, new UpdateFeed(feed), Path.Combine(dir, "cache"));
        await updater.CheckAsync();
        Assert.True(await updater.DownloadAsync(), updater.Message);
        Assert.True(await updater.ApplyAsync(restart: false, Environment.ProcessId), updater.Message);
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(app, "Karvon")));
        Assert.Equal(["Karvon Studio.app"], Directory.GetFileSystemEntries(Path.Combine(dir, "Applications")).Select(Path.GetFileName));
        Directory.Delete(dir, recursive: true);
    }
}
