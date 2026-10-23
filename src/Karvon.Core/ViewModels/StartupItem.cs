using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>
/// What a folder starts, chosen on the toolbar in place of a startup project: a launch.json configuration (F5 debugs it,
/// Ctrl+F5 runs it without the debugger), or a run task (Ctrl+F5 runs it; F5 debugs it when its module knows how).
/// </summary>
public sealed record StartupItem(string Name, string Source, WorkspaceTask? Task, DebugLaunch? Debug)
{
    public static StartupItem Of(DebugLaunch launch) => new(launch.Name, launch.Source, null, launch);

    public static StartupItem Of(WorkspaceTask task) => new(task.Name, task.Source, task, task.Debug);

    public override string ToString() => Name;
}
