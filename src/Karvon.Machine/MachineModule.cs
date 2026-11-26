using Avalonia.Controls.Templates;
using Karvon.Claude;
using Karvon.Machine.ViewModels;
using Karvon.Machine.Views;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Machine;

/// <summary>
/// This Computer: an explorer of its sections (see <see cref="MachineExplorerViewModel"/>), each opening its page in the
/// document well as Docker Explorer's do: the ports it listens on, each with the process that holds it; its processes, by
/// the CPU and memory they use; its CPU, memory, disks and network. Claude's tools to see them, and to stop a process
/// (asking the user first).
/// </summary>
public sealed class MachineModule : KarvonModule, IMachineHost
{
    public const string ToolWindowId = "Machine";

    MachineExplorerViewModel? _explorer;

    public override string Id => "machine";

    public override string Name => "This Computer";

    public override string Icon => "console";

    public override string Description =>
        "This computer's listening ports (each with the process that holds it), its processes (by the CPU and memory they use) and "
        + "its CPU, memory, disks and network, each a page as Docker Explorer's; Claude's tools to see them, and to stop a process.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "This Computer", "This _Computer", "ToolMachine", DockSide.Right, () => new MachineExplorerView { DataContext = Explorer })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
        [Document<MachineDocument>(_ => new MachineDocumentView()), Document<MachineFileDocument>(_ => new MachineFileView())];

    /// <summary>The explorer, made the first time it is needed (nothing is read before).</summary>
    public MachineExplorerViewModel Explorer => _explorer ??= new MachineExplorerViewModel(this);

    public override IEnumerable<IClaudeTool> ClaudeTools() =>
        [new MachinePortsTool(Listening.All), new MachineProcessesTool(), new MachineStopProcessTool(), new MachineResourcesTool()];

    /// <summary>A command that lists the listening ports: Karvon's tool instead.</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("listening ports", @"^\s*(sudo\s+)?(ss\s+-\w*l\w*|netstat\s+-\w*[la]\w*|lsof\s+(.*\s)?-i\b)", Use),
        ClaudeShellRule.Command("processes", @"^\s*(ps\s+(aux|-ef|-e\b|-A\b)|top\s+-b\b|htop\b)", UseProcesses),
    ];

    const string Use = "Karvon lists the ports this computer listens on, with the process that holds each: use mcp__karvon__machine_ports "
        + "(give port to see whether one is taken, and by what), not ss, netstat or lsof.";

    const string UseProcesses = "Karvon lists this computer's processes, by the CPU or memory they use, with the ports each listens on: use "
        + "mcp__karvon__machine_processes (and machine_stop_process to stop one: the user is asked), not ps or top.";

    Task<bool?> IMachineHost.ConfirmAsync(string title, string message) => Workbench.ConfirmAsync(title, message);

    void IMachineHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    bool IMachineHost.IsShown => Workbench.IsToolWindowShowing(ToolWindowId);

    IEnumerable<DocumentTab> IMachineHost.Documents => Workbench.Documents;

    void IMachineHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IMachineHost.OpenFile(string path) => Workbench.OpenFile(path);

    public override void Dispose() => _explorer?.Dispose();
}
