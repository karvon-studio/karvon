using Karvon.Services;

namespace Karvon.Tests;

/// <summary>A path is below a folder after one of its separators, never by a longer name that starts the same.</summary>
public class FilePathsTests
{
    [Fact]
    public void A_path_is_under_a_folder_after_its_separator()
    {
        Assert.True(FilePaths.IsUnder("/repo/src/app.ts", "/repo"));
        Assert.True(FilePaths.IsUnder("/repo/src/app.ts", "/repo/"));
        Assert.False(FilePaths.IsUnder("/repository/app.ts", "/repo"));
        Assert.False(FilePaths.IsUnder("/repo", "/repo"));
        Assert.False(FilePaths.IsUnder("/repo/", "/repo"));
        Assert.True(FilePaths.IsSameOrUnder("/repo", "/repo"));
        Assert.True(FilePaths.IsSameOrUnder("/repo/a", "/repo"));
        Assert.True(FilePaths.IsUnder("/a", "/"));
        // Linux and macOS tell the case apart, as their file systems do by default here; Windows does not.
        Assert.Equal(OperatingSystem.IsWindows(), FilePaths.IsUnder("/Repo/a", "/repo"));
        Assert.Equal(OperatingSystem.IsWindows(), FilePaths.IsUnder(@"C:\repo\a.cs", @"C:\repo"));
    }
}
