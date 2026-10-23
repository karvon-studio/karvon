namespace Karvon.Testing;

public enum TestState
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <summary>What a Test Explorer row is.</summary>
public enum TestNodeKind
{
    Project,
    Namespace,
    Class,
    Method,
    Test,
}

/// <summary>A row a test is under in Test Explorer: its project, its namespace or file, its class or describe block, a theory's method.</summary>
public sealed record TestGroup(TestNodeKind Kind, string Name);

/// <summary>A test a framework found: its id (its framework's own), the rows it is under (outermost first), its name, where it is.</summary>
public sealed record TestItem(string Id, IReadOnlyList<TestGroup> Groups, string Name, string? File, int Line)
{
    /// <summary>Its name with what it is in, as a report gives it (Namespace.Class.Method(row), "area > of nothing > is zero").</summary>
    public string FullName { get; init; } = "";

    /// <summary>What holds it: a .NET test's assembly, a test file.</summary>
    public string? Container { get; init; }
}

/// <summary>
/// A test's result, by its id (Running while it runs). A framework that finds a test only as it runs it (Vitest's skipped
/// ones) gives the test too.
/// </summary>
public sealed record TestItemResult(string Id, TestState State, TimeSpan Duration, string? ErrorMessage, string? StackTrace, string? Output)
{
    public TestItem? Item { get; init; }
}

/// <summary>The tests a framework found; when there are none, why (the build failed, it is not installed...).</summary>
public sealed record TestDiscovery(IReadOnlyList<TestItem> Tests, string? Problem = null)
{
    public static TestDiscovery Failed(string problem) => new([], problem);
}

/// <summary>
/// A test framework of the workspace: xUnit, NUnit and MSTest through vstest (the .NET module's), Vitest (the
/// TypeScript module's). Test Explorer shows the tests of each, runs them and, when the framework can, debugs them.
/// </summary>
public abstract class TestFramework : IDisposable
{
    /// <summary>Unique in the workspace ("dotnet", "vitest:web"): its tests' ids are its own.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    public virtual bool CanDebug => false;

    /// <summary>Whether the workspace has its tests now (.NET's: the solution has a test project); Test Explorer leaves it out until then.</summary>
    public virtual bool IsAvailable => true;

    /// <summary>Its tests, built first when it needs that.</summary>
    public abstract Task<TestDiscovery> DiscoverAsync(CancellationToken ct);

    /// <summary>These of its tests (null: all of them), under the debugger when asked; each result as it comes.</summary>
    public abstract Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct);

    public virtual void Dispose()
    {
    }
}
