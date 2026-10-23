namespace Karvon.Modules;

/// <summary>
/// A project of a workload that is no .NET project (a folder with its package.json): in a solution, Solution Explorer
/// shows it beside the solution's projects, with its files as a folder's.
/// </summary>
/// <param name="Directory">Its folder.</param>
/// <param name="File">The file that makes it one (its package.json): its icon is that file's.</param>
public sealed record WorkspaceProject(string Name, string Directory, string File)
{
    /// <summary>What it is, beside its name ("npm").</summary>
    public string Kind { get; init; } = "";
}
