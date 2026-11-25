using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.CodeMap;

/// <summary>
/// Claude's tools on the Code Map: the workspace's tree with what each part is for, a file's declarations, and a search of
/// all of it by name, word, path, signature, doc comment and summary. The map is brought up to date before each answer, so
/// it is never older than the workspace.
/// </summary>
static class CodeMapTools
{
    public static IEnumerable<IClaudeTool> Create(Func<CodeMapWorkspace?> map) => [new CodeMapTool(map), new CodeSearchTool(map)];

    /// <summary>The map, up to date (or as far as the first scan went, said in the answer's first line).</summary>
    internal static async Task<(CodeMapWorkspace Map, string Note)> FreshAsync(Func<CodeMapWorkspace?> map, CancellationToken cancellationToken)
    {
        var workspace = map() ?? throw new InvalidOperationException("No workspace is open in Karvon.");
        await workspace.FreshAsync(cancellationToken);
        var note = "";
        if (!workspace.Indexer.IsComplete && workspace.Indexer.Progress is { Total: > 0 } progress)
            note = $"(The Code Map is still being made: {progress.Done} of {progress.Total} files read; what is not read yet is not here.)\n";
        return (workspace, note);
    }

    /// <summary>
    /// A path Claude gave as the map has it: relative to the workspace (with ./ or not, or from its root as /src), absolute,
    /// with a / at its end or \ in it; "" is the workspace itself. One the map does not have is said so.
    /// </summary>
    internal static string PathOf(CodeMapWorkspace map, string? given)
    {
        var path = (given ?? "").Trim().Replace('\\', '/');
        if (path.TrimEnd('/') is "" or ".")
            return "";
        string? Inside(string full)
        {
            var relative = Path.GetRelativePath(map.Root, full).Replace('\\', '/').TrimEnd('/');
            return relative == "." ? "" : relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
        }
        var found = Path.IsPathRooted(path) ? Inside(Path.GetFullPath(path)) ?? Inside(Path.GetFullPath(Path.Combine(map.Root, path.TrimStart('/'))))
            : Inside(Path.GetFullPath(Path.Combine(map.Root, path)));
        if (found is null || !map.Store.Has(found))
            throw new InvalidOperationException(
                $"{given} is not a folder or a file of the map (dependencies, ignored files and build output are left out); code_search without a path finds by name.");
        return found;
    }

    abstract class Tool(Func<CodeMapWorkspace?> map) : IClaudeTool
    {
        protected Func<CodeMapWorkspace?> Map => map;

        public abstract string Name { get; }

        public abstract string Description { get; }

        public abstract JsonObject InputSchema { get; }

        public abstract ClaudeToolPlan Plan(JsonElement input);

        public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
        {
            try
            {
                return await RunAsync(input, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return ClaudeToolOutput.Error(ex.Message);
            }
        }

        protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);
    }

    /// <summary><c>code_map</c>: a folder's tree (the workspace's, by default) or a file's declarations, with what each is for.</summary>
    sealed class CodeMapTool(Func<CodeMapWorkspace?> map) : Tool(map)
    {
        public override string Name => "code_map";

        public override string Description =>
            "The workspace's map, kept up to date by Karvon: a folder's tree (the workspace's by default) with each folder's file count "
            + "and each folder's and file's one-line summary (or its first declarations), or, for a file, its declarations with their "
            + "lines, signatures and doc comments. Start here to learn a project or a part of it, instead of ls, find, tree or reading "
            + "files to see what they are; then code_search to find something by name or by what it does, and code_read_symbol to read it.";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{
              "path":{"type":"string","description":"A folder or a file of the workspace, relative to it (the workspace itself when left out)."},
              "depth":{"type":"integer","description":"How many levels of a folder to show (default 2, at most 6)."}}}
            """);

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Code Map", Str(input, "path") is { Length: > 0 } p ? p : null, "");

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var (map, note) = await FreshAsync(Map, cancellationToken);
            var path = PathOf(map, Str(input, "path"));
            if (map.Store.Files().TryGetValue(path, out var file))
                return new ClaudeToolOutput(note + CodeMapText.Outline(map.Store, file));
            return new ClaudeToolOutput(note + CodeMapText.Tree(map.Store, map.Root, path, Int(input, "depth", 2, 1, 6)));
        }
    }

    /// <summary><c>code_search</c>: the map searched, best first.</summary>
    sealed class CodeSearchTool(Func<CodeMapWorkspace?> map) : Tool(map)
    {
        public override string Name => "code_search";

        public override string Description =>
            "Searches Karvon's Code Map of the workspace, at once: declarations (types, members, functions), files and folders, by the start "
            + "of any word of their names (OrderTotals is found by 'totals' or 'order tot', Type.Member by 'type member'), their paths, "
            + "signatures, doc comments and summaries (what they are for: 'invoice tax rate'). Best first: path:line: kind name — doc or "
            + "summary. Use it instead of grep or find to find where something is or what does something; read what it finds with "
            + "code_read_symbol or code_map (a file's declarations).";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Words of a name, a path or what it does."},
              "kind":{"type":"string","enum":["declaration","file","folder"],"description":"Only declarations, files or folders."},
              "path":{"type":"string","description":"Only under this folder of the workspace."},
              "limit":{"type":"integer","description":"At most this many (default 25, at most 100)."}},
             "required":["query"]}
            """);

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Code Search", Str(input, "query"), "");

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var query = Str(input, "query") is { Length: > 0 } q ? q : throw new InvalidOperationException("Give the query: words of a name, a path or what it does.");
            var (map, note) = await FreshAsync(Map, cancellationToken);
            var hits = map.Store.Search(query, Str(input, "kind"), PathOf(map, Str(input, "path")), Int(input, "limit", 25, 1, 100));
            return new ClaudeToolOutput(note + (hits.Count == 0 ? $"Nothing in the Code Map matches {query}." : CodeMapText.Hits(hits)));
        }
    }
}
