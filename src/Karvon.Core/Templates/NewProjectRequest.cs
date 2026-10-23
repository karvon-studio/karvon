namespace Karvon.Templates;

/// <summary>Everything the "Create a new project" pages collect.</summary>
public sealed record NewProjectRequest(
    ProjectTemplate Template,
    string ProjectName,
    string Location,
    string SolutionName,
    bool SolutionInSameDirectory,
    IReadOnlyList<(TemplateOption Option, string Value)> Options)
{
    public string SolutionDirectory => Path.Combine(Location, SolutionInSameDirectory ? ProjectName : SolutionName);

    public string ProjectDirectory => SolutionInSameDirectory ? SolutionDirectory : Path.Combine(SolutionDirectory, ProjectName);

    /// <summary>Why the request cannot be created, or null when it can.</summary>
    public static string? Validate(string projectName, string location, string solutionName, bool sameDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return "Enter a project name.";
        if (!IsValidName(projectName))
            return "The project name contains characters that are not allowed.";
        if (!sameDirectory && string.IsNullOrWhiteSpace(solutionName))
            return "Enter a solution name.";
        if (!sameDirectory && !IsValidName(solutionName))
            return "The solution name contains characters that are not allowed.";
        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathRooted(location))
            return "Enter a full path for the location.";
        var target = Path.Combine(location, sameDirectory ? projectName : solutionName);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return $"The directory \"{target}\" already exists and is not empty.";
        return null;
    }

    static bool IsValidName(string name) =>
        name.Trim() == name && name is not ("." or "..") && name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
}
