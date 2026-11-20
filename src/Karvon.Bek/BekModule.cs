using System.Text.Json;
using Karvon.Bek.ViewModels;
using Karvon.Bek.Views;
using Karvon.Claude;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.Bek;

/// <summary>
/// Bek OS apps: an app of the phone's (a WebAssembly component of its world) previewed live by bek-preview, bek-sdk's tool,
/// in place of the phone in an emulator. Its page is drawn by the phone's own code, so what the window shows is what the
/// phone shows; a new build of the component starts it again. Claude Code's tools see the same screen and act on it.
/// </summary>
public sealed partial class BekModule : KarvonModule, IPreviewHost
{
    public const string ToolWindowId = "BekPreview";

    /// <summary>Where cargo puts a Rust app's component, by its build's profile.</summary>
    static readonly string[] BuiltComponents = [Path.Combine("target", "wasm32-wasip2", "release"), Path.Combine("target", "wasm32-wasip2", "debug")];

    PreviewViewModel? _preview;

    /// <summary>The module's pane of the Output window: what bek-preview says, from its own threads.</summary>
    Karvon.ViewModels.OutputChannel? _output;

    public override string Id => "bek";

    public override string Name => "Bek OS Apps";

    public override string Icon => "folder-app";

    public override string Description =>
        "Apps of Bek OS's phone previewed live by bek-preview (bek-sdk): the page as the phone draws it, taps, scrolling, typing "
        + "and Back, the theme, the language and the page's size, a new build started at once; its elements, styles and store in "
        + "the browser; and Claude Code's preview tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Bek Preview", "Bek Pre_view", "Eye", DockSide.Right, () => new PreviewView { DataContext = EnsurePreview() })];

    /// <summary>The window's view model, made the first time it is needed (on the UI thread, with its Output pane).</summary>
    public PreviewViewModel EnsurePreview()
    {
        _output ??= Workbench.Output("Bek Preview");
        return _preview ??= new PreviewViewModel(this);
    }

    /// <summary>An app's component, or its folder (a manifest beside a cargo build): Preview on the Phone.</summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(ExplorerNode node) =>
        ComponentOf(node.Path) is var (component, manifest)
            ? [new("Preview on the Phone", () => OpenAsync(component, manifest))]
            : [];

    /// <summary>
    /// The component a path names and the manifest that goes with it: a <c>.wasm</c> itself (its manifest found beside it by
    /// bek-preview), or an app's folder, with its <c>manifest</c> and the newest component cargo built for it.
    /// </summary>
    internal static (string Component, string? Manifest)? ComponentOf(string? path)
    {
        if (path is null)
            return null;
        if (File.Exists(path) && path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
            return (path, null);
        var manifest = Path.Combine(path, "manifest");
        if (!Directory.Exists(path) || !File.Exists(manifest))
            return null;
        var built = BuiltComponents
            .Select(folder => Path.Combine(path, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.wasm"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return built is null ? null : (built, manifest);
    }

    /// <summary>The app shown in the Bek Preview window, which comes to the front.</summary>
    public async Task OpenAsync(string component, string? manifest)
    {
        Workbench.ShowToolWindow(ToolWindowId);
        try
        {
            await EnsurePreview().OpenAsync(component, manifest);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Status(ex.Message);
        }
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => BekClaudeTools.Create(EnsurePreview, () => Workbench.Workspace);

    /// <summary>What Claude does shows where the user would look: the Bek Preview window, beside Claude Code.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (tool is "bek_preview_open" or "bek_preview_act")
            Workbench.ShowToolWindowBesideClaude(ToolWindowId);
        return Task.CompletedTask;
    }

    /// <summary>The solution closes: its app's preview ends with it.</summary>
    public override async Task<bool> CloseWorkspaceAsync()
    {
        if (_preview is not null)
            await _preview.StopAsync();
        return true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_preview is not null)
            await _preview.DisposeAsync();
        _preview = null;
    }

    // ---- IPreviewHost -------------------------------------------------------------------------------------------

    void IPreviewHost.Said(string line) => _output?.Append(line);

    void IPreviewHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IPreviewHost.Status(string text) => Workbench.Status(text);

    async Task<string?> IPreviewHost.PickComponentAsync() =>
        (await Workbench.PickFilesAsync("An App's Component (.wasm)")).FirstOrDefault(file => file.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase));
}
