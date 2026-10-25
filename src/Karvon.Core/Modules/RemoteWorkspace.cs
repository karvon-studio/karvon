namespace Karvon.Modules;

/// <summary>
/// A workspace whose files are a copy of a machine's folder (the Servers module's remote folders): its commands run
/// there. Karvon runs its tasks and builds as <see cref="Run"/> makes them (ssh to the machine, in its folder), opens the
/// terminal there (<see cref="Terminal"/>, for a folder of the copy), reads the paths they print as the copy's (<see cref="LocalPath"/>), and lets
/// it send what changed here first (<see cref="FlushAsync"/>).
/// </summary>
public sealed record RemoteWorkspace(string Name, string Description)
{
    public required Func<WorkspaceTask, WorkspaceTask> Run { get; init; }

    public required Func<string, (string Program, IReadOnlyList<string> Arguments)> Terminal { get; init; }

    public Func<string, string> LocalPath { get; init; } = path => path;

    public Func<Task> FlushAsync { get; init; } = () => Task.CompletedTask;
}
