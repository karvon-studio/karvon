using Karvon.Editor;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>
/// A folder's packages installed by its language's package manager, as VS Code offers it (a virtual environment and pip
/// for a requirements.txt, npm install for a package.json): asked once a session as a module's language server starts
/// for a folder whose packages are not there (a no is kept with the solution's state), and offered by Ctrl+. on an
/// import the server cannot resolve. It runs in Output (Package Manager); then the language servers start again.
/// </summary>
public sealed partial class MainViewModel
{
    const string RestoreDeclinedKey = "packages.declined";
    readonly HashSet<string> _restoreAsked = new(StringComparer.Ordinal);
    readonly HashSet<string> _restoring = new(StringComparer.Ordinal);

    IEnumerable<string> RestoreDeclined => (_state.Modules.GetValueOrDefault(RestoreDeclinedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>As a module's language server starts for a folder: whether to install its packages, when they are not there.</summary>
    internal async Task OfferPackageRestoreAsync(KarvonModule module, string folder)
    {
        if (module.PackageRestoreIn(folder) is not { IsMissing: true } restore || RestoreDeclined.Contains(module.Id) || !_restoreAsked.Add($"{module.Id}|{folder}"))
            return;
        var answer = await ((IWorkbench)this).ConfirmAsync("Install packages",
            $"This folder's packages are not installed, so the {module.Name} language server cannot resolve their imports.\n\n"
            + $"{restore.Title}? It runs in Output (Package Manager), and IntelliSense starts again with them when it is done.");
        if (answer == true)
            await RestorePackagesAsync(module, folder);
        else if (answer == false)
            _state.Modules[RestoreDeclinedKey] = string.Join(',', RestoreDeclined.Append(module.Id).Distinct());
    }

    /// <summary>A module's package restore run in a folder (its steps in Output), then the language servers started again.</summary>
    internal async Task<bool> RestorePackagesAsync(KarvonModule module, string folder)
    {
        if (module.PackageRestoreIn(folder) is not { } restore || !_restoring.Add(folder))
            return false;
        try
        {
            if (restore.Steps is not { } steps)
            {
                SetStatus(restore.Problem ?? $"{restore.Title}: there is nothing to run it with.");
                return false;
            }
            var workbench = (IWorkbench)this;
            var output = workbench.Output("Package Manager");
            workbench.ShowOutput(output);
            foreach (var step in steps)
            {
                output.Append($"> {step.Program} {string.Join(' ', step.Arguments)}");
                int code;
                try
                {
                    code = await workbench.RunAsync(step, output.Append, CancellationToken.None);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    output.Append(ex.Message);
                    code = -1;
                }
                if (code != 0)
                {
                    output.Append($"{step.Name} failed (exit code {code}).");
                    SetStatus($"{step.Name} failed (exit code {code}): see Output (Package Manager).");
                    return false;
                }
            }
            output.Append($"{restore.Title}: done.");
            SetStatus($"{restore.Title}: done. IntelliSense starts again with the packages.");
            await RestartLanguageServer();
            return true;
        }
        finally
        {
            _restoring.Remove(folder);
        }
    }

    /// <summary>Ctrl+. on an import a module's server cannot resolve: its package restore, for the open folder.</summary>
    internal IReadOnlyList<EditorQuickAction> PackageQuickActions(string file, IReadOnlyList<LspDiagnostic> diagnostics) =>
        Solution?.Directory is { } folder
            ? [.. Modules.Where(m => diagnostics.Any(m.IsUnresolvedImport)).Select(m => (Module: m, Restore: m.PackageRestoreIn(folder)))
                .Where(x => x.Restore is not null).Select(x => new EditorQuickAction(x.Restore!.Title, () => RestorePackagesAsync(x.Module, folder)))]
            : [];
}
