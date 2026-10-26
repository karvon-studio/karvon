namespace Karvon.Modules;

/// <summary>
/// How a folder's packages get installed by its language's package manager, where its tools do not do it themselves
/// (a virtual environment and pip install -r requirements.txt; npm install): asked once a session as the module's
/// language server starts for a folder whose packages are not there, and offered by Ctrl+. on an import it cannot resolve.
/// </summary>
/// <param name="Title">What it does, as a menu item and a question say it ("Install the packages (npm install)").</param>
/// <param name="IsMissing">The packages are not there at all (it is asked about only then).</param>
/// <param name="Steps">Its commands, in order, each after the one before succeeded; null when it has nothing to run them with.</param>
public sealed record PackageRestore(string Title, bool IsMissing, IReadOnlyList<WorkspaceTask>? Steps)
{
    /// <summary>Why it cannot run, when it has no steps ("Python was not found: install Python 3").</summary>
    public string? Problem { get; init; }
}
