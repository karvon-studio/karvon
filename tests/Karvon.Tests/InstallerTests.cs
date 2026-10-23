using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Karvon.Updates;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Karvon Studio Installer, as Visual Studio Installer: the installed product and its update, Modify's workloads and components.</summary>
public class InstallerTests
{
    /// <summary>An updater of a .tar.gz install of 0.1.0, with a release of 0.2.0 in a feed of its own.</summary>
    internal static (Updater Updater, string Dir) UpdaterWithRelease(string notes = "## What's new\n\n- The installer.")
    {
        var dir = Directory.CreateTempSubdirectory("karvon-installer-").FullName;
        var files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
        var asset = "karvon-studio-0.2.0-linux-x64.tar.gz";
        var build = Directory.CreateDirectory(Path.Combine(dir, "build", "karvon-0.2.0")).FullName;
        File.WriteAllText(Path.Combine(build, "Karvon"), "0.2.0");
        using (var gz = new System.IO.Compression.GZipStream(File.Create(Path.Combine(files, asset)), System.IO.Compression.CompressionLevel.Fastest))
            System.Formats.Tar.TarFile.CreateFromDirectory(build, gz, includeBaseDirectory: true);
        File.WriteAllText(Path.Combine(files, "SHA256SUMS"), $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(files, asset))))}  {asset}\n");
        object File_(string name) => new { name, browser_download_url = new Uri(Path.Combine(files, name)).AbsoluteUri, size = new FileInfo(Path.Combine(files, name)).Length };
        var feed = Path.Combine(dir, "releases.json");
        File.WriteAllText(feed, JsonSerializer.Serialize(new[]
        {
            new { tag_name = "v0.2.0", name = "Karvon Studio 0.2.0", body = notes, html_url = "https://example.invalid/v0.2.0", draft = false, prerelease = false, assets = new[] { File_(asset), File_("SHA256SUMS") } },
        }));
        Directory.CreateDirectory(Path.Combine(dir, "apps", "app"));
        return (new Updater(new AppInstall(Distribution.Tar, "0.1.0", Path.Combine(dir, "apps", "app")), new UpdateFeed(feed), Path.Combine(dir, "cache")), dir);
    }

    static T Named<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task Tools_get_tools_and_features_modifies_the_workloads_and_components()
    {
        var vm = NewViewModel();
        vm.Settings.DisabledModules = ["kotlin"];
        var main = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        main.Show();
        var window = main.ShowInstaller(InstallerPage.Modify, check: false)!;
        var installer = Assert.IsType<InstallerViewModel>(window.DataContext);
        try
        {
            // Modify: the modules as workloads, languages and tools, each with its icon; Kotlin, turned off, unchecked.
            Assert.True(Named<DockPanel>(window, "ModifyPage").IsVisible);
            Assert.Equal("Modifying — Karvon Studio — " + AppInstall.CurrentVersion, installer.ModifyTitle);
            Assert.Equal(["Languages", "Tools"], installer.Workloads.Select(g => g.Title));
            Assert.Equal([".NET", "TypeScript and JavaScript", "Python", "Go", "Rust", "C and C++", "Java", "Kotlin"], installer.Workloads[0].Items.Select(i => i.Name));
            Assert.All(installer.Workloads.SelectMany(g => g.Items), w => Assert.NotNull(w.Icon));
            var kotlin = installer.Workloads[0].Items.Single(i => i.Name == "Kotlin");
            Assert.False(kotlin.IsSelected);
            Assert.False(installer.HasChanges);
            Assert.Equal("Total space required: 0 KB", installer.SpaceText);

            // Checked: its components download now, as Installation details and the space required say.
            kotlin.ToggleCommand.Execute(null);
            Assert.True(installer.HasChanges);
            var kotlinLsp = installer.ComponentGroups.Single(g => g.Title == "Kotlin").Items.Single();
            if (!kotlinLsp.IsInstalled)
            {
                Assert.Equal(["Kotlin: turned on: Kotlin language server (kotlin-lsp) downloads now"], installer.Changes.Select(c => $"{c.Text}: {string.Join(", ", c.Items)}"));
                Assert.Equal("Total space required: about 1.1 GB", installer.SpaceText);
                Assert.True(kotlinLsp.IsSelected);
            }
            // Unchecked again: nothing changes. Another unchecked: off when Karvon Studio starts again.
            kotlin.ToggleCommand.Execute(null);
            Assert.False(installer.HasChanges);
            Assert.Equal(["Database Explorer", "Docker Explorer", "Servers", "MCP Servers", "Skills", "Claude Code"], installer.Workloads[1].Items.Select(i => i.Name));
            installer.Workloads[1].Items.Single(i => i.Name == "Docker Explorer").IsSelected = false;
            Assert.Equal(["Docker Explorer: turned off when Karvon Studio starts again"], installer.Changes.Select(c => c.Text));
            Snapshot(window, "installer-modify");

            // Individual components, searched.
            installer.ShowTabCommand.Execute(InstallerTab.Components);
            Assert.True(Named<DockPanel>(window, "ComponentsView").IsVisible);
            installer.ComponentFilter = "debugger";
            Assert.Equal([".NET", "TypeScript and JavaScript", "Python", "Go", "Rust", "Java", "Kotlin"], installer.ComponentGroups.Where(g => g.IsVisible).Select(g => g.Title));
            Assert.Equal(["Native debugger (CodeLLDB) " + Karvon.Lldb.CodeLldb.Version], installer.ComponentGroups.Single(g => g.Title == "Rust").Items.Where(i => i.IsVisible).Select(i => i.Name));
            installer.ComponentFilter = "";
            // CodeLLDB is Rust's and C and C++'s: listed once.
            Assert.Single(installer.ComponentGroups.SelectMany(g => g.Items), i => i.Component.Id == "codelldb");

            // Installation locations; Modify keeps the change for when Karvon Studio starts again.
            installer.ShowTabCommand.Execute(InstallerTab.Locations);
            Assert.Equal(Karvon.Modules.ModuleComponent.DataRoot, installer.ComponentsLocation);
            await installer.ApplyCommand.ExecuteAsync(null);
            Assert.Contains("docker", vm.Settings.DisabledModules);
            Assert.Equal(("Done. The changes take effect when Karvon Studio starts again.", true), (installer.ApplyText, installer.RestartNeeded));
            installer.ShowProductCommand.Execute(null);
            Assert.True(Named<Button>(window, "RestartButton").IsVisible);

            // Open again: the same window.
            Assert.Same(window, main.ShowInstaller(InstallerPage.Product, check: false));
        }
        finally
        {
            vm.Settings.DisabledModules = [];
            vm.Settings.Save();
            window.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_product_says_its_update_downloads_it_and_the_status_bar_says_so()
    {
        var (updater, dir) = UpdaterWithRelease();
        var vm = NewViewModel();
        vm.Updates = updater;
        var main = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        main.Show();
        var badge = Named<Button>(main, "UpdateBadge");
        Assert.False(badge.IsVisible);

        // Help > Check for Updates: the installer, checking; the update and its notes.
        var window = main.ShowInstaller(InstallerPage.Product, check: true)!;
        var installer = Assert.IsType<InstallerViewModel>(window.DataContext);
        await WaitUntil(() => updater.State == UpdateState.Available && installer.CanUpdate, TimeSpan.FromSeconds(10));
        Assert.True(Named<Grid>(window, "ProductPage").IsVisible);
        Assert.Equal("0.1.0 · Linux .tar.gz", Named<TextBlock>(window, "VersionText").Text);
        Assert.Equal("Karvon Studio 0.2.0 is available.", Named<TextBlock>(window, "UpdateMessage").Text);
        Assert.Equal(("Update", true), (Named<Button>(window, "UpdateButton").Content, Named<Button>(window, "UpdateButton").IsVisible));
        Assert.Equal("# Karvon Studio 0.2.0\n\n## What's new\n\n- The installer.", installer.ReleaseNotes);
        await WaitUntil(() => badge.IsVisible, TimeSpan.FromSeconds(5));
        Assert.Equal("Karvon Studio 0.2.0 is available", vm.UpdateBadge);
        Assert.False(Named<Button>(window, "LaunchButton").IsVisible);
        Snapshot(window, "installer");

        // Update: downloaded and checked; then Restart and update, which the status bar says too.
        await installer.UpdateCommand.ExecuteAsync(null);
        await WaitUntil(() => installer.UpdateButtonText == "Restart and update", TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateState.Ready, updater.State);
        await WaitUntil(() => vm.UpdateBadge == "Restart to update", TimeSpan.FromSeconds(5));
        ExitAction? asked = null;
        installer.RestartRequested += a => asked = a;
        await installer.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(ExitAction.RestartUpdated, asked);

        // Update settings, kept.
        installer.UpdatesInstallOnClose = false;
        Assert.False(vm.Settings.UpdatesInstallOnClose);
        installer.UpdatesInstallOnClose = true;
        window.Close();
        main.Close();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Karvon_checks_once_a_day_and_installs_as_it_closes_as_its_settings_say()
    {
        var (updater, dir) = UpdaterWithRelease();
        var settings = new Karvon.Services.Settings { InMemory = true };

        // Checked as it starts, the update downloaded; not again the same day.
        await AppUpdates.StartAsync(updater, settings);
        Assert.Equal(UpdateState.Ready, updater.State);
        Assert.NotNull(settings.UpdatesLastCheck);
        var (other, otherDir) = UpdaterWithRelease();
        settings.UpdatesLastCheck = DateTimeOffset.Now.AddHours(-2);
        await AppUpdates.StartAsync(other, settings);
        Assert.Equal(UpdateState.Idle, other.State);
        // Without downloading: only said.
        settings.UpdatesLastCheck = null;
        settings.UpdatesDownload = false;
        await AppUpdates.StartAsync(other, settings);
        Assert.Equal(UpdateState.Available, other.State);
        // Not checked when the settings say not to.
        var (off, offDir) = UpdaterWithRelease();
        settings.UpdatesCheck = false;
        settings.UpdatesLastCheck = null;
        await AppUpdates.StartAsync(off, settings);
        Assert.Equal(UpdateState.Idle, off.State);
        foreach (var folder in new[] { dir, otherDir, offDir })
            Directory.Delete(folder, recursive: true);
    }
}
