namespace Karvon.Modules;

/// <summary>Where Karvon gets its modules: those it comes with (the application says which), later those installed on their own too.</summary>
public static class ModuleCatalog
{
    /// <summary>A new instance of each, in the order they show; none until the application sets it.</summary>
    public static Func<IReadOnlyList<KarvonModule>> Create { get; set; } = () => [];
}
