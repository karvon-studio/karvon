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

    /// <summary>Where cargo puts a Rust app's component in a target folder, by its build's profile.</summary>
    static readonly string[] BuiltComponents = [Path.Combine("wasm32-wasip2", "release"), Path.Combine("wasm32-wasip2", "debug")];

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

    /// <summary>An app's component, or its folder (a manifest and a cargo build): Preview on the Phone. Not on any WebAssembly (a web app's).</summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(ExplorerNode node) =>
        node.Path is { } path && (Directory.Exists(path) || IsAppComponent(path)) && ComponentOf(path) is var (component, manifest)
            ? [new("Preview on the Phone", () => OpenAsync(component, manifest))]
            : [];

    /// <summary>
    /// Whether a <c>.wasm</c> is a Bek app's: cargo's build of one (<c>wasm32-wasip2/release</c> or <c>debug</c> of a target
    /// folder), or one with its manifest beside it (<c>name.manifest</c>).
    /// </summary>
    internal static bool IsAppComponent(string file)
    {
        if (!file.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            return false;
        if (File.Exists(Path.ChangeExtension(file, ".manifest")))
            return true;
        var profile = Path.GetDirectoryName(file);
        return Path.GetFileName(profile) is "release" or "debug" && Path.GetFileName(Path.GetDirectoryName(profile)) == "wasm32-wasip2";
    }

    /// <inheritdoc cref="ComponentOf(string?, string?)"/>
    internal static (string Component, string? Manifest)? ComponentOf(string? path) => ComponentOf(path, Environment.GetEnvironmentVariable("CARGO_TARGET_DIR"));

    /// <summary>
    /// The component a path names and the manifest that goes with it: a <c>.wasm</c> itself (its manifest found beside it by
    /// bek-preview), or an app's folder, with its <c>manifest</c> and the newest component cargo built for it: in its own
    /// target folder, its cargo workspace's, or <paramref name="cargoTarget"/> (CARGO_TARGET_DIR), by its crate's name.
    /// </summary>
    internal static (string Component, string? Manifest)? ComponentOf(string? path, string? cargoTarget)
    {
        if (path is null)
            return null;
        if (File.Exists(path) && path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
            return (path, null);
        var manifest = Path.Combine(path, "manifest");
        if (!Directory.Exists(path) || !File.Exists(manifest))
            return null;
        var own = Path.Combine(Path.GetFullPath(path), "target");
        // cargo names a component after its crate (its lib's name when it has one), with dashes as underscores.
        var crate = CrateName(path)?.Replace('-', '_');
        var built = TargetFolders(path, cargoTarget)
            .SelectMany(target => BuiltComponents.Select(profile => Path.Combine(target, profile)))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.wasm"))
            // In a target folder crates share, only this one's; with no Cargo.toml to say which, only its own folder's.
            .Where(file => crate is not null
                ? string.Equals(Path.GetFileNameWithoutExtension(file), crate, StringComparison.Ordinal)
                : file.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return built is null ? null : (built, manifest);
    }

    /// <summary>Where cargo may have built the app: CARGO_TARGET_DIR, its own target folder, then those of the folders above it up to its workspace's.</summary>
    static IEnumerable<string> TargetFolders(string app, string? cargoTarget)
    {
        if (cargoTarget is { Length: > 0 } && Path.IsPathRooted(cargoTarget))
            yield return cargoTarget;
        for (var folder = new DirectoryInfo(Path.GetFullPath(app)); folder is not null; folder = folder.Parent)
        {
            yield return Path.Combine(folder.FullName, "target");
            if (folder.FullName != Path.GetFullPath(app) && IsWorkspaceRoot(folder.FullName))
                yield break;
        }
    }

    static bool IsWorkspaceRoot(string folder) => ReadToml(folder).Any(line => line == "[workspace]");

    /// <summary>The crate's name in its Cargo.toml: its [lib]'s when it names one, else its [package]'s; null without one.</summary>
    static string? CrateName(string folder)
    {
        string? section = null, package = null, lib = null;
        foreach (var line in ReadToml(folder))
        {
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (System.Text.RegularExpressions.Regex.Match(line, "^name\\s*=\\s*\"([^\"]+)\"") is { Success: true } name)
            {
                if (section == "[package]")
                    package ??= name.Groups[1].Value;
                else if (section == "[lib]")
                    lib ??= name.Groups[1].Value;
            }
        }
        return lib ?? package;
    }

    /// <summary>A folder's Cargo.toml, a trimmed line each; none when it has none (or it cannot be read).</summary>
    static IEnumerable<string> ReadToml(string folder)
    {
        var toml = Path.Combine(folder, "Cargo.toml");
        try
        {
            return File.Exists(toml) ? [.. File.ReadLines(toml).Select(line => line.Trim())] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
