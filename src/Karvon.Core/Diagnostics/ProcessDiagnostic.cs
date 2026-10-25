namespace Karvon.Diagnostics;

/// <summary>A process of the program running: its pid, name and command line (what tells a runtime's apart).</summary>
public sealed record DiagnosedProcess(int Pid, string Name, string CommandLine)
{
    public string Label => $"{Name} ({Pid})";
}

/// <summary>A row of a diagnostic's report: a type's or a function's name, where it is from, and its two numbers.</summary>
public sealed record DiagnosticRow(string Name, string Detail, string First, string Second);

/// <summary>What a diagnostic found, as a table: its columns' headers, its rows by how much they weigh.</summary>
public sealed record DiagnosticReport(string Title, string Summary, string NameHeader, string FirstHeader, string SecondHeader, IReadOnlyList<DiagnosticRow> Rows);

/// <summary>A diagnostic run on a process: for how long (a trace's), what it says as it goes, and how it asks the user.</summary>
public sealed record DiagnosticRun(DiagnosedProcess Process, TimeSpan Duration, Action<string> Log, Func<string, string, Task<bool>> Confirm);

/// <summary>
/// What a module can find out about a process of the program running, as Visual Studio's Diagnostic Tools do: .NET's
/// memory snapshot (dotnet-gcdump) and CPU profile (dotnet-trace). Its files are deleted once it has read them.
/// </summary>
public abstract class ProcessDiagnostic
{
    /// <summary>The button's text: "Take Memory Snapshot".</summary>
    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>Whether a trace of it lasts for the run's duration (the duration is asked).</summary>
    public virtual bool IsTimed => false;

    /// <summary>Whether it can look at the process (one of its runtime's).</summary>
    public abstract bool Applies(DiagnosedProcess process);

    public abstract Task<DiagnosticReport> RunAsync(DiagnosticRun run, CancellationToken ct);
}
