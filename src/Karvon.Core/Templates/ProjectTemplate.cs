namespace Karvon.Templates;

public sealed record TemplateChoice(string Value, string Label, string? Description);

public enum TemplateOptionKind
{
    Choice,
    Bool,
}

/// <summary>
/// A template parameter shown on "Additional information". <see cref="Invert"/> means the checkbox is the
/// opposite of the template symbol (VS shows "Configure for HTTPS" for the NoHttps symbol, for example).
/// </summary>
public sealed record TemplateOption(
    string Symbol,
    string CliName,
    string Label,
    string? Description,
    TemplateOptionKind Kind,
    IReadOnlyList<TemplateChoice> Choices,
    string? DefaultValue,
    bool Invert = false)
{
    /// <summary>The checkbox state when nothing was chosen.</summary>
    public bool DefaultChecked => string.Equals(DefaultValue, "true", StringComparison.OrdinalIgnoreCase) != Invert;

    /// <summary>The <c>dotnet new</c> arguments for a value (a choice value, or "true"/"false" for the checkbox).</summary>
    public IEnumerable<string> Arguments(string value)
    {
        if (Kind == TemplateOptionKind.Choice)
            return [CliName, value];
        var symbolValue = bool.Parse(value) != Invert;
        return [CliName, symbolValue ? "true" : "false"];
    }
}

/// <summary>Where New Project's templates come from: a module's (".NET", "Go"), listed the first time the wizard shows them.</summary>
public sealed record ProjectTemplateSource(string Name, Func<CancellationToken, Task<IReadOnlyList<ProjectTemplate>>> LoadAsync);

/// <summary>
/// A project template of a module: one of the .NET SDK's (one entry per template group and language), a Go module, a
/// Cargo package...
/// </summary>
public sealed record ProjectTemplate(
    string Identity,
    string GroupIdentity,
    string Name,
    string ShortName,
    string Description,
    string? Author,
    string? Language,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Platforms,
    int Order,
    string DefaultName,
    IReadOnlyList<TemplateOption> Options)
{
    /// <summary>The template's own icon (extracted from its package), when it ships one.</summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// How it makes the project: what it returns opens. A folder template (a Go module, a Cargo package...) makes the
    /// request's project folder, which is there already, and returns it; one that <see cref="MakesSolution"/> (.NET's
    /// dotnet new) makes the solution around the project in the location, and returns the solution file.
    /// </summary>
    public Func<NewProjectRequest, Action<string>, CancellationToken, Task<string>>? Create { get; init; }

    /// <summary>It makes a solution around the project: the wizard asks for its name and where it goes.</summary>
    public bool MakesSolution { get; init; }

    /// <summary>
    /// Add > New Project, when the template can add to an open solution: it makes the project in the request's project
    /// folder and returns its project files, which the solution adds. Null for a template whose project is no project of
    /// a solution (a Go module): Add > New Project leaves it out.
    /// </summary>
    public Func<NewProjectRequest, Action<string>, CancellationToken, Task<IReadOnlyList<string>>>? AddToSolution { get; init; }

    /// <summary>A module's template: its project folder is all it makes.</summary>
    public static ProjectTemplate OfModule(string identity, string name, string language, string description, string defaultName,
        IReadOnlyList<string> tags, Func<NewProjectRequest, Action<string>, CancellationToken, Task<string>> create) =>
        new(identity, identity, name, identity, description, "Karvon", language, tags, ["Linux", "macOS", "Windows"], 0, defaultName, []) { Create = create };

    /// <summary>Language first, then platforms and project types, as VS lists them under a template.</summary>
    public IReadOnlyList<string> AllTags => [.. (Language is null ? [] : new[] { Language }), .. Platforms, .. Tags];

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || ShortName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || AllTags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
