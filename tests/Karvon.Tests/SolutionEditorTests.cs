using Karvon.Workspace;

namespace Karvon.Tests;

/// <summary>Solution Explorer's changes to .sln and .slnx files, read back as Karvon reads a solution.</summary>
public class SolutionEditorTests
{
    static string Copy(string format)
    {
        var dir = Path.GetDirectoryName(SolutionExplorerTests.CopySample())!;
        var sln = Path.Combine(dir, "BlazorSample.sln");
        if (format == "slnx")
        {
            File.WriteAllText(Path.Combine(dir, "BlazorSample.slnx"), """
                <Solution>
                  <Folder Name="/Solution Items/">
                    <File Path="README.md" />
                  </Folder>
                  <Project Path="Components/Components.csproj" />
                  <Project Path="Tests/Tests.csproj" />
                  <Project Path="Web/Web.csproj" />
                </Solution>
                """);
            File.Delete(sln);
            return Path.Combine(dir, "BlazorSample.slnx");
        }
        return sln;
    }

    static string NewProject(string solution, string relative)
    {
        var path = Path.Combine(Path.GetDirectoryName(solution)!, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return path;
    }

    static ExplorerNode Child(ExplorerNode node, string name) => node.Children.Single(c => c.Name == name);

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public async Task Adds_moves_and_removes_projects_folders_and_items(string format)
    {
        var sln = Copy(format);
        var dir = Path.GetDirectoryName(sln)!;
        var lib = NewProject(sln, "src/Lib/Lib.csproj");

        // Projects, in the solution or in a solution folder (made as needed); twice is once.
        await SolutionEditor.AddProjectsAsync(sln, [lib], "/libs/");
        await SolutionEditor.AddProjectsAsync(sln, [lib], "/libs/");
        var loaded = await SolutionLoader.LoadAsync(sln);
        Assert.Equal(4, loaded.Projects.Count);
        var libs = Child(loaded.Root, "libs");
        Assert.Equal(NodeKind.SolutionFolder, libs.Kind);
        Assert.Equal("/libs/", libs.SolutionFolder);
        var libNode = Child(libs, "Lib");
        Assert.Equal(lib, libNode.Path);
        Assert.Equal("/libs/", libNode.SolutionFolder);
        Assert.Null(Child(loaded.Root, "Web").SolutionFolder);
        Assert.Contains(format == "sln" ? @"src\Lib\Lib.csproj" : "src/Lib/Lib.csproj", File.ReadAllText(sln));

        // Folders: nested, renamed, refused when the name is taken or not a name.
        var nested = await SolutionEditor.AddFolderAsync(sln, "/libs/", "Core");
        Assert.Equal("/libs/Core/", nested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SolutionEditor.AddFolderAsync(sln, "/libs/", "Core"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SolutionEditor.AddFolderAsync(sln, null, "a/b"));
        Assert.Equal("/libs/Shared/", await SolutionEditor.RenameFolderAsync(sln, nested, "Shared"));
        Assert.Equal("/packages/", await SolutionEditor.RenameFolderAsync(sln, "/libs/", "packages"));
        loaded = await SolutionLoader.LoadAsync(sln);
        var packages = Child(loaded.Root, "packages");
        Assert.Equal("/packages/Shared/", Child(packages, "Shared").SolutionFolder);
        Assert.Equal("/packages/", Child(packages, "Lib").SolutionFolder);

        // Solution items: added where they are, renamed, removed (the files stay).
        File.WriteAllText(Path.Combine(dir, "build.props"), "<Project />");
        await SolutionEditor.AddFilesAsync(sln, SolutionEditor.SolutionItems, [Path.Combine(dir, "build.props")]);
        await SolutionEditor.AddFilesAsync(sln, "/packages/Shared/", [Path.Combine(dir, "Web", "Program.cs")]);
        loaded = await SolutionLoader.LoadAsync(sln);
        var items = Child(loaded.Root, "Solution Items");
        Assert.Equal(["build.props", "README.md"], items.Children.Select(c => c.Name).Order());
        Assert.All(items.Children, c => Assert.Equal(SolutionEditor.SolutionItems, c.SolutionFolder));
        Assert.Equal(Path.Combine(dir, "Web", "Program.cs"), Child(Child(Child(loaded.Root, "packages"), "Shared"), "Program.cs").Path);
        await SolutionEditor.MoveFileAsync(sln, SolutionEditor.SolutionItems, Path.Combine(dir, "build.props"), Path.Combine(dir, "Directory.Build.props"));
        await SolutionEditor.RemoveFileAsync(sln, SolutionEditor.SolutionItems, Path.Combine(dir, "README.md"));
        loaded = await SolutionLoader.LoadAsync(sln);
        Assert.Equal(["Directory.Build.props"], Child(loaded.Root, "Solution Items").Children.Select(c => c.Name));
        Assert.True(File.Exists(Path.Combine(dir, "README.md")));

        // A project's new file name; removing a project or a folder keeps the files.
        var renamed = Path.Combine(dir, "src", "Lib", "Library.csproj");
        File.Move(lib, renamed);
        await SolutionEditor.MoveProjectAsync(sln, lib, renamed);
        loaded = await SolutionLoader.LoadAsync(sln);
        Assert.Equal(renamed, Child(Child(loaded.Root, "packages"), "Library").Path);
        await SolutionEditor.RemoveProjectAsync(sln, Path.Combine(dir, "Tests", "Tests.csproj"));
        await SolutionEditor.RemoveFolderAsync(sln, "/packages/");
        loaded = await SolutionLoader.LoadAsync(sln);
        Assert.Equal(["Components", "Web"], loaded.Projects.Select(p => p.Name).Order());
        Assert.DoesNotContain(loaded.Root.Children, c => c.Name == "packages");
        Assert.True(File.Exists(renamed));
        Assert.True(File.Exists(Path.Combine(dir, "Tests", "Tests.csproj")));

        // What VS writes stays (the configurations of the projects kept).
        if (format == "sln")
        {
            var text = File.ReadAllText(sln);
            Assert.StartsWith("Microsoft Visual Studio Solution File, Format Version 12.00", text.TrimStart('﻿', '\r', '\n'));
            Assert.Contains("{4E5A3D2B-1C0F-4B7A-9E61-2F3C4D5E6A71}.Release|Any CPU.Build.0 = Release|Any CPU", text);
        }
    }

    [Fact]
    public async Task A_project_opened_on_its_own_has_no_solution_to_change()
    {
        var sln = Copy("sln");
        var web = Path.Combine(Path.GetDirectoryName(sln)!, "Web", "Web.csproj");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SolutionEditor.AddFolderAsync(web, null, "docs"));
    }
}
