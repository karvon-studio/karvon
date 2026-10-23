namespace Karvon.Workspace;

public sealed record PackageRef(string Name, string? Version);

public sealed record LaunchProfile(
    string Name,
    string? ApplicationUrl,
    bool LaunchBrowser,
    string? LaunchUrl,
    string? CommandLineArgs,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

/// <summary>
/// A solution's project, as Solution Explorer, the build, the tests and F5 know it: what its module read of its project file
/// (.NET's: an SDK-style .csproj, by ProjectReader).
/// </summary>
public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public string Sdk { get; init; } = "";
    public string? OutputType { get; init; }
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];
    public IReadOnlyList<PackageRef> Packages { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
    public IReadOnlyList<LaunchProfile> LaunchProfiles { get; init; } = [];
    public bool IsTestProject { get; init; }

    public bool IsWeb => Sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

    public bool IsRunnable => !IsTestProject && (IsWeb
        || string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}
