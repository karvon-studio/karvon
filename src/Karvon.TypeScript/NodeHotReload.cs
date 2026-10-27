using System.Text.RegularExpressions;

namespace Karvon.TypeScript;

/// <summary>
/// Hot Reload of a Node.js program Karvon runs (HotReload/node-hot-reload.js): the agent, loaded before the program by
/// NODE_OPTIONS, applies a saved change to a file of the workspace the program loaded, in place, and asks for a restart
/// for one it cannot apply. A dev server or a watcher that reloads by itself (Vite, Next.js, nodemon, tsx watch...) gets
/// none: its own reload does the work, and Karvon restarts nothing for it.
/// </summary>
static partial class NodeHotReload
{
    /// <summary>The agent, beside Karvon's assemblies.</summary>
    public static string Agent => Path.Combine(AppContext.BaseDirectory, "HotReload", "node-hot-reload.js");

    /// <summary>
    /// The variables that load the agent into the programs of a workspace (null without the agent's file): its
    /// --require after the NODE_OPTIONS there are (a path with forward slashes, which NODE_OPTIONS does not read as escapes).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Environment(string workspace, string? nodeOptions) =>
        File.Exists(Agent)
            ? new Dictionary<string, string>
            {
                ["NODE_OPTIONS"] = string.Join(' ', new[] { nodeOptions, $"--require \"{Agent.Replace('\\', '/')}\"" }.Where(o => !string.IsNullOrWhiteSpace(o))),
                ["KARVON_HOT_RELOAD_ROOT"] = workspace,
            }
            : null;

    /// <summary>A command that reloads by itself: a dev server with its own hot module replacement, or a watcher that restarts.</summary>
    [GeneratedRegex(@"(^|[\s;&|(])(vite|nodemon|parcel|ts-node-dev|tsnd|webpack-dev-server|react-scripts\s+start|vue-cli-service\s+serve|ng\s+serve|next\s+dev|nux[ti]\s+dev|astro\s+dev|remix(\s+vite:)?\s+dev|svelte-kit\s+dev|webpack\s+serve|tsx\s+watch|electron-vite\s+dev|expo\s+start|gatsby\s+develop|storybook\s+dev)(\s|$)|\s--(watch|hot)\b")]
    private static partial Regex SelfReloading();

    public static bool ReloadsItself(string command) => SelfReloading().IsMatch(" " + command);
}
