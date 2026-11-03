using Karvon.Services;

namespace Karvon.Tests;

/// <summary>The settings file, as several Karvon Studios read and write it.</summary>
public class SettingsTests
{
    [Fact]
    public void Each_Karvon_Studio_writes_what_it_changed_and_keeps_what_the_other_did()
    {
        var file = Path.Combine(Path.GetTempPath(), "karvon-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        var a = Settings.Load(file);
        var b = Settings.Load(file);

        // Line blame turned off in one, then the other saves (still thinking it is on): it stays off.
        a.LineBlame = false;
        a.Save();
        b.Minimap = true;
        b.Save();
        var read = Settings.Load(file);
        Assert.False(read.LineBlame);
        Assert.True(read.Minimap);

        // Inside an object, member by member; what both changed, as the later one left it.
        a.Layout.LeftWidth = 300;
        a.EditorZoom = 1.5;
        a.Save();
        b.Layout.BottomHeight = 200;
        b.EditorZoom = 0.8;
        b.Save();
        read = Settings.Load(file);
        Assert.Equal((300, 200, 0.8), (read.Layout.LeftWidth, read.Layout.BottomHeight, read.EditorZoom));

        // A solution's time added in one, another solution removed in the other: both as they left them.
        a.AddRecent("/src/a.sln");
        a.AddRecent("/src/old.sln");
        a.Save();
        var c = Settings.Load(file);
        c.RemoveRecent("/src/old.sln");
        c.Save();
        b.RecentOpened["/src/b.sln"] = DateTime.Now;
        b.Save();
        read = Settings.Load(file);
        Assert.Equal(["/src/a.sln", "/src/b.sln"], read.RecentOpened.Keys.Order());
        Assert.Equal(["/src/a.sln"], read.RecentSolutions);
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }
}
