using Karvon.Workspace;

namespace Karvon.DotNet.Templates;

/// <summary>
/// The item templates Visual Studio offers in Add New Item for C# and Blazor projects, writing what VS's templates
/// write (file-scoped namespace from the project's root namespace and the folder, as `dotnet new class` does).
/// </summary>
public static class CSharpItemTemplates
{
    static string CSharp(string ns, string body) => $"namespace {ns};\n\n{body}\n";

    public static IReadOnlyList<ItemTemplate> All { get; } =
    [
        new("Class", "An empty class definition", ".cs", "Class", (ns, name) => CSharp(ns, $"public class {name}\n{{\n}}")),
        new("Interface", "An empty interface definition", ".cs", "Interface", (ns, name) => CSharp(ns, $"public interface {name}\n{{\n}}")),
        new("Record", "An empty record definition", ".cs", "Record", (ns, name) => CSharp(ns, $"public record {name};")),
        new("Struct", "An empty struct definition", ".cs", "Struct", (ns, name) => CSharp(ns, $"public struct {name}\n{{\n}}")),
        new("Enum", "An enumeration", ".cs", "Enum", (ns, name) => CSharp(ns, $"public enum {name}\n{{\n}}")),
        new("Razor Component", "A Razor component for Blazor", ".razor", "Component", (_, name) => $"<h3>{name}</h3>\n\n@code {{\n\n}}\n"),
        new("Code File", "An empty C# code file", ".cs", "CodeFile", (_, _) => ""),
    ];

    public static ItemTemplate Class => All[0];

    public static ItemTemplate RazorComponent => All.First(t => t.Extension == ".razor");
}
