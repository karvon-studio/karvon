namespace Karvon.Modules;

/// <summary>The modules Karvon Studio comes with. Later, those installed on their own join them.</summary>
public static class ModuleCatalog
{
    /// <summary>A new instance of each, in the order they show on the activity bar and in the View menu.</summary>
    public static IReadOnlyList<KarvonModule> Create() => [new Mcp.McpModule(), new Skills.SkillsModule()];
}
