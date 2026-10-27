using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Lsp;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

/// <summary>A declaration a code_ tool read (its source, from its first line to its last): Karvon shows it in the editor.</summary>
public sealed record FoundCode(string Path, int StartLine, int EndLine);

/// <summary>
/// Claude's code navigation through the language servers Karvon runs (Roslyn, TypeScript's, Pyright...): declarations
/// by name, a file's outline, a declaration's source, what a name refers to, its references and implementations. What
/// grep and sed find by the text, these find by what the code means, in one call.
/// </summary>
static class CodeClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(MainViewModel vm) =>
    [
        new CodeSymbolsTool(vm),
        new CodeOutlineTool(vm),
        new CodeReadSymbolTool(vm),
        new CodeDefinitionTool(vm),
        new CodeReferencesTool(vm),
        new CodeImplementationsTool(vm),
    ];
}

/// <summary>A declaration a name resolved to: its server, its file, its node of the file's outline and the names it is in.</summary>
sealed record Declaration(LspClient Server, string File, LspDocumentSymbol Symbol, string QualifiedName);

abstract class CodeTool(MainViewModel vm) : IdeTool(vm)
{
    /// <summary>How long a call waits for a server that is still loading the workspace (Roslyn, its projects).</summary>
    static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Lines of one declaration's source a call gives at most.</summary>
    protected const int MaxSourceLines = 400;

    /// <summary>Files a call opened in a server, not in the editor (one that answers only for open files), and how many calls have each.</summary>
    static readonly Dictionary<(LspClient, string), int> Opened = [];

    /// <summary>The server of a file of the solution, started if need be, and ready.</summary>
    protected async Task<LspClient> ServerOfAsync(string file, CancellationToken cancellationToken)
    {
        var lsp = await Vm.StartLanguageServerForClaudeAsync(file)
            ?? throw new InvalidOperationException($"No language server serves {Relative(file)} in Karvon; use find_in_files and open_document for it.");
        await ReadyAsync(lsp, cancellationToken);
        return lsp;
    }

    /// <summary>The workspace's servers (a file's, when given), ready.</summary>
    protected async Task<IReadOnlyList<LspClient>> ServersAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is not null)
            return [await ServerOfAsync(ExistingFile(path), cancellationToken)];
        var servers = await Vm.LanguageServersForClaudeAsync();
        if (servers.Count == 0)
            throw new InvalidOperationException("No language server runs for this workspace yet: give the path of a file in the language to start its server.");
        foreach (var lsp in servers)
            await ReadyAsync(lsp, cancellationToken);
        return servers;
    }

    static async Task ReadyAsync(LspClient lsp, CancellationToken cancellationToken)
    {
        if (lsp.IsReady)
            return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => ready.TrySetResult();
        lsp.Ready += OnReady;
        try
        {
            if (!lsp.IsReady)
                await ready.Task.WaitAsync(ReadyTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("IntelliSense is still loading the workspace (see the status bar); try again in a minute.");
        }
        finally
        {
            lsp.Ready -= OnReady;
        }
    }

    protected string ExistingFile(string path)
    {
        var file = FileOf(path);
        if (!File.Exists(file))
            throw new InvalidOperationException($"{Relative(file)} does not exist.");
        return file;
    }

    /// <summary>
    /// A request about a file: asked as it is (Roslyn and Pyright answer for any file of the workspace), and again with the
    /// file open in the server when that finds nothing (TypeScript's answers only for open files).
    /// </summary>
    protected async Task<IReadOnlyList<T>> AskAboutAsync<T>(LspClient lsp, string file, Func<Task<IReadOnlyList<T>>> ask, CancellationToken cancellationToken)
    {
        if (Vm.Editors.Any(d => d.FilePath == file))
            return await ask();
        try
        {
            if (await ask() is { Count: > 0 } found)
                return found;
        }
        catch (LspException)
        {
        }
        var key = (lsp, file);
        if (Opened.TryGetValue(key, out var count))
            Opened[key] = count + 1;
        else
        {
            Opened[key] = 1;
            await lsp.DidOpenAsync(file, await File.ReadAllTextAsync(file, cancellationToken));
        }
        try
        {
            return await ask();
        }
        finally
        {
            Opened[key]--;
            if (Opened[key] == 0)
            {
                Opened.Remove(key);
                // Opened in the editor meanwhile: the editor closes it.
                if (!Vm.Editors.Any(d => d.FilePath == file))
                    await lsp.DidCloseAsync(file);
            }
        }
    }

    protected Task<IReadOnlyList<LspDocumentSymbol>> OutlineAsync(LspClient lsp, string file, CancellationToken cancellationToken) =>
        AskAboutAsync(lsp, file, () => lsp.DocumentSymbolsAsync(file, cancellationToken), cancellationToken);

    /// <summary>A file's lines as the editor has it (unsaved changes included), else as it is on disk.</summary>
    protected async Task<string[]> LinesOfAsync(string file, CancellationToken cancellationToken)
    {
        var open = Vm.Editors.FirstOrDefault(d => d.FilePath == file);
        var text = open?.Host.Document.Text ?? await File.ReadAllTextAsync(file, cancellationToken);
        return [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
    }

    /// <summary>A file of the solution's folder that a server's location is in, or null (a decompiled or generated one).</summary>
    protected string? SolutionFileOf(LspLocation location)
    {
        if (!location.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;
        var file = location.FilePath;
        return Services.FilePaths.IsUnder(file, SolutionDirectory) && File.Exists(file) ? file : null;
    }

    // ---- Names -------------------------------------------------------------------------------------------------

    /// <summary>A declaration's name without its parameters, type parameters or type (Roslyn's: Add(int, int) : int).</summary>
    protected static string BaseName(string name)
    {
        var end = name.IndexOfAny(['(', '<', '[', ' ', ':']);
        return (end > 0 ? name[..end] : name).Trim();
    }

    /// <summary>A name as Claude writes it (Namespace.Type.Member, Type::member, Type#member, List&lt;T&gt;.Add(T)) as its parts, each a base name.</summary>
    protected static string[] PartsOf(string symbol)
    {
        var text = symbol.Trim();
        var parameters = text.IndexOf('(');
        if (parameters >= 0)
            text = text[..parameters];
        for (var before = ""; before != text;)
        {
            before = text;
            text = Regex.Replace(text, "<[^<>]*>", "");
        }
        return [.. text.Replace("::", ".").Replace('#', '.').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// What a server says a declaration is in, as a name: Roslyn's "in Circle (project app (net10.0))" is Circle, and its
    /// "project app (net10.0)" (a type's) nothing.
    /// </summary>
    protected static string? ContainerOf(LspSymbol symbol)
    {
        if (symbol.Container is not { Length: > 0 } text || text.StartsWith("project ", StringComparison.Ordinal))
            return null;
        text = Regex.Replace(text, @"\s*\(project [^()]*(\([^()]*\))?[^()]*\)$", "");
        if (text.StartsWith("in ", StringComparison.Ordinal))
            text = text[3..];
        return text.Length > 0 ? text : null;
    }

    /// <summary>Whether the names a declaration is in end as Claude's do, as far as both go (Roslyn's container may be its type alone).</summary>
    protected static bool IsIn(IReadOnlyList<string> containers, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0)
            return true;
        var overlap = Math.Min(containers.Count, wanted.Count);
        if (overlap == 0)
            return false;
        for (var i = 1; i <= overlap; i++)
            if (!string.Equals(containers[^i], wanted[^i], StringComparison.Ordinal))
                return false;
        return true;
    }

    static bool IsScope(LspSymbolKind kind) => kind is LspSymbolKind.File or LspSymbolKind.Module or LspSymbolKind.Namespace or LspSymbolKind.Package;

    protected static string KindOf(LspSymbolKind kind) => kind switch
    {
        LspSymbolKind.EnumMember => "enum member",
        LspSymbolKind.TypeParameter => "type parameter",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The outline's path to a position: its declarations from the outermost to the innermost that holds it.</summary>
    protected static List<LspDocumentSymbol> PathTo(IReadOnlyList<LspDocumentSymbol> outline, LspPosition position)
    {
        var path = new List<LspDocumentSymbol>();
        for (var level = outline; level.FirstOrDefault(s => Contains(s.Range, position)) is { } holder; level = holder.Children)
            path.Add(holder);
        return path;
    }

    protected static bool Contains(LspRange range, LspPosition position) =>
        Compare(range.Start, position) <= 0 && Compare(position, range.End) <= 0;

    static int Compare(LspPosition a, LspPosition b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);

    /// <summary>
    /// What a declaration path is called, its namespaces left out, and the type (Roslyn's) of the last only when asked:
    /// Greeting.Name, GreetingTests.Adds(int, int, int) : void.
    /// </summary>
    protected static string QualifiedName(IEnumerable<LspDocumentSymbol> path, bool type = true)
    {
        var names = path.Where(s => !IsScope(s.Kind)).Select(s => s.Name).ToList();
        return string.Join(".", names.Select((n, i) => type && i == names.Count - 1 ? n : WithoutType(n)));
    }

    /// <summary>A declaration's name without the type Roslyn's names end with: Total(IEnumerable&lt;IShape&gt;) for Total(IEnumerable&lt;IShape&gt;) : double.</summary>
    static string WithoutType(string name)
    {
        var type = name.IndexOf(" : ", name.LastIndexOf(')') + 1, StringComparison.Ordinal);
        return type > 0 ? name[..type] : name;
    }

    /// <summary>The member a position is in (for a reference's line), or null outside any.</summary>
    protected static string? MemberAt(IReadOnlyList<LspDocumentSymbol> outline, LspPosition position) =>
        QualifiedName(PathTo(outline, position), type: false) is { Length: > 0 } name ? name : null;

    /// <summary>
    /// The declarations a name is: in a file's outline when a path is given, else those the workspace's servers find by
    /// it (workspace/symbol), each found in its file's outline. Exact names first; their case ignored when none is exact.
    /// </summary>
    protected async Task<List<Declaration>> DeclarationsAsync(string symbol, string? path, CancellationToken cancellationToken)
    {
        var parts = PartsOf(symbol);
        if (parts.Length == 0)
            throw new InvalidOperationException("Give the name of a declaration (Type, Type.Member).");
        var name = parts[^1];
        var wanted = parts[..^1];
        var found = new List<Declaration>();
        var outlines = new Dictionary<string, IReadOnlyList<LspDocumentSymbol>>();
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            if (path is not null)
            {
                var file = ExistingFile(path);
                var lsp = await ServerOfAsync(file, cancellationToken);
                var outline = outlines.TryGetValue(file, out var known) ? known : outlines[file] = await OutlineAsync(lsp, file, cancellationToken);
                Walk(outline, []);

                void Walk(IReadOnlyList<LspDocumentSymbol> level, List<LspDocumentSymbol> above)
                {
                    foreach (var s in level)
                    {
                        if (string.Equals(BaseName(s.Name), name, comparison) && IsIn([.. above.Select(a => BaseName(a.Name))], wanted))
                            found.Add(new Declaration(lsp, file, s, QualifiedName([.. above, s])));
                        Walk(s.Children, [.. above, s]);
                    }
                }
            }
            else
                foreach (var lsp in await ServersAsync(null, cancellationToken))
                    foreach (var s in await lsp.WorkspaceSymbolsAsync(name, cancellationToken))
                    {
                        if (!string.Equals(BaseName(s.Name), name, comparison) || SolutionFileOf(s.Location) is not { } file)
                            continue;
                        var outline = outlines.TryGetValue(file, out var known) ? known : outlines[file] = await OutlineAsync(lsp, file, cancellationToken);
                        var at = PathTo(outline, s.Location.Range.Start);
                        // The innermost declaration of that name where the server says (its name's range, or all of it).
                        var index = at.FindLastIndex(d => string.Equals(BaseName(d.Name), name, comparison));
                        if (index < 0 || !IsIn([.. at.Take(index).Select(a => BaseName(a.Name))], wanted))
                            continue;
                        if (found.Any(d => d.File == file && d.Symbol.Range == at[index].Range))
                            continue;
                        found.Add(new Declaration(lsp, file, at[index], QualifiedName(at.Take(index + 1))));
                    }
            if (found.Count > 0)
                break;
        }
        return found;
    }

    /// <summary>
    /// Where a name is in the code, to ask about it: on a line of a file (as written there, declared or used), or where it
    /// is declared. Several declarations of the name are listed for Claude to say which.
    /// </summary>
    protected async Task<(LspClient Server, string File, LspPosition Position, string Name)> PositionOfAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol") ?? throw new InvalidOperationException("Give the symbol: its name (Type.Member), with the path and line it is on.");
        var path = Str(input, "path");
        if (path is not null && Int(input, "line", 0, 0, int.MaxValue) is var line and > 0)
        {
            var file = ExistingFile(path);
            var lines = await LinesOfAsync(file, cancellationToken);
            if (line > lines.Length)
                throw new InvalidOperationException($"{Relative(file)} has {lines.Length} lines.");
            var text = lines[line - 1];
            var name = PartsOf(symbol) is { Length: > 0 } parts ? parts[^1] : symbol;
            var column = Int(input, "column", 0, 0, int.MaxValue) - 1;
            if (column < 0)
            {
                var match = Regex.Match(text, $@"(?<![\w$@]){Regex.Escape(name)}(?![\w$])");
                if (!match.Success)
                    throw new InvalidOperationException($"{name} is not on line {line} of {Relative(file)}: {text.Trim()}");
                column = match.Index;
            }
            return (await ServerOfAsync(file, cancellationToken), file, new LspPosition(line - 1, column), symbol);
        }
        var declarations = await DeclarationsAsync(symbol, path, cancellationToken);
        if (declarations.Count == 0)
            throw new InvalidOperationException($"No declaration is named {symbol}; code_symbols finds names that match.");
        if (declarations.Select(d => d.QualifiedName).Distinct().Count() > 1)
            throw new InvalidOperationException($"{symbol} is declared {declarations.Count} times; give the path and line of the one you mean:\n"
                + string.Join("\n", declarations.Select(d => $"{Relative(d.File)}:{d.Symbol.SelectionRange.Start.Line + 1}: {KindOf(d.Symbol.Kind)} {d.QualifiedName}")));
        var only = declarations[0];
        return (only.Server, only.File, only.Symbol.SelectionRange.Start, only.QualifiedName);
    }

    // ---- What a call gives -------------------------------------------------------------------------------------

    /// <summary>
    /// A declaration's source with line numbers, from its doc comment and attributes (the lines right above it that are
    /// comments or attributes) to its end; a long one cut short.
    /// </summary>
    protected (string Text, FoundCode Code) Source(string file, string[] lines, LspDocumentSymbol symbol, string heading, int maxLines = MaxSourceLines)
    {
        var first = Math.Min(symbol.Range.Start.Line, lines.Length - 1);
        while (first > 0 && IsDecoration(lines[first - 1]))
            first--;
        var last = Math.Min(symbol.Range.End.Line, lines.Length - 1);
        var shownLast = Math.Min(last, first + maxLines - 1);
        var text = new StringBuilder($"{Relative(file)}:{first + 1}{(last > first ? $"-{last + 1}" : "")}, {heading}:");
        for (var i = first; i <= shownLast; i++)
            text.Append('\n').Append(i + 1).Append('\t').Append(lines[i]);
        if (shownLast < last)
            text.Append($"\n… lines {shownLast + 2}-{last + 1} not shown: read them with open_document, or a member with code_read_symbol (Type.Member); code_outline lists the members.");
        return (text.ToString(), new FoundCode(file, first + 1, last + 1));
    }

    static bool IsDecoration(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal) || text.StartsWith('*')
            || text.StartsWith('[') || text.StartsWith('@') || text.StartsWith("#[", StringComparison.Ordinal);
    }

    /// <summary>A line as a result shows it: trimmed, a long one cut short.</summary>
    protected static string Shown(string line)
    {
        var text = line.Trim();
        return text.Length > 200 ? text[..199] + "…" : text;
    }
}

/// <summary><c>code_symbols</c>: the workspace's declarations whose names match (Go to All, Ctrl+T).</summary>
sealed class CodeSymbolsTool(MainViewModel vm) : CodeTool(vm)
{
    public override string Name => "code_symbols";

    public override string Description =>
        "Finds the solution's declarations (types, methods, properties, fields, functions...) by name through Karvon's language servers (Roslyn for C#), "
        + "as path:line: kind Container.Name. Use it instead of grep for 'class X' or 'X(' to find where something is declared. A query of Type.Member finds a type's member; "
        + "the server also matches parts of names (camel humps).";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"The name or part of it; Type.Member for a member of a type."},
          "kind":{"type":"string","description":"Only declarations of this kind: class, interface, struct, enum, method, function, property, field, constructor, event..."},
          "path":{"type":"string","description":"A file of the language to search with (its server is started if need be); omit to search with every running server."},
          "limit":{"type":"integer","description":"At most this many (default 50)."}},
         "required":["query"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Find Symbol", Str(input, "query"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var query = Str(input, "query") ?? throw new InvalidOperationException("Give the name to find.");
        var parts = PartsOf(query);
        if (parts.Length == 0)
            throw new InvalidOperationException("Give the name to find.");
        var kind = Str(input, "kind")?.Replace(" ", "");
        var limit = Int(input, "limit", 50, 1, 500);
        var found = new List<(string File, LspSymbol Symbol)>();
        foreach (var lsp in await ServersAsync(Str(input, "path"), cancellationToken))
            foreach (var s in await lsp.WorkspaceSymbolsAsync(parts[^1], cancellationToken))
                if (SolutionFileOf(s.Location) is { } file
                    && (kind is null || string.Equals(s.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                    && IsIn(PartsOf(ContainerOf(s) ?? ""), parts[..^1])
                    && !found.Any(f => f.File == file && f.Symbol.Location.Range == s.Location.Range))
                    found.Add((file, s));
        if (found.Count == 0)
            return new ClaudeToolOutput($"No declaration's name matches {query}.");
        // The exact name first, then those it starts, then the rest; each by its file.
        var ranked = found.OrderBy(f => BaseName(f.Symbol.Name) == parts[^1] ? 0 : BaseName(f.Symbol.Name).StartsWith(parts[^1], StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(f => Relative(f.File), StringComparer.Ordinal).ThenBy(f => f.Symbol.Location.Range.Start.Line).ToList();
        var shown = ranked.Take(limit).ToList();
        var detail = new List<FoundText>();
        var text = new StringBuilder();
        foreach (var (file, s) in shown)
        {
            var line = s.Location.Range.Start.Line + 1;
            var container = ContainerOf(s) is { } c ? c + "." : "";
            text.Append(text.Length > 0 ? "\n" : "").Append($"{Relative(file)}:{line}: {KindOf(s.Kind)} {container}{s.Name}");
            detail.Add(new FoundText(file, line, s.Location.Range.Start.Character + 1, $"{KindOf(s.Kind)} {container}{s.Name}"));
        }
        if (ranked.Count > limit)
            text.Append($"\n(only the first {limit} of {ranked.Count})");
        return new ClaudeToolOutput(text.ToString()) { Detail = detail };
    }
}

/// <summary><c>code_outline</c>: a file's declarations as a tree, each with its lines.</summary>
sealed class CodeOutlineTool(MainViewModel vm) : CodeTool(vm)
{
    public override string Name => "code_outline";

    public override string Description =>
        "The declarations of a source file as a tree (namespaces, types, their members), each with its first and last line and its kind: "
        + "what a file has before reading it. Then read one with code_read_symbol, or lines with open_document.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"path":{"type":"string","description":"The file, relative to the solution's folder."}},"required":["path"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Outline", Str(input, "path"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var file = ExistingFile(Str(input, "path") ?? throw new InvalidOperationException("Give the path of the file."));
        var lsp = await ServerOfAsync(file, cancellationToken);
        var outline = await OutlineAsync(lsp, file, cancellationToken);
        var lines = await LinesOfAsync(file, cancellationToken);
        if (outline.Count == 0)
            return new ClaudeToolOutput($"{Relative(file)} ({lines.Length} lines) declares nothing the language server lists.");
        var text = new StringBuilder($"{Relative(file)} ({lines.Length} lines):");
        Append(outline, 0);
        return new ClaudeToolOutput(text.ToString());

        void Append(IReadOnlyList<LspDocumentSymbol> level, int depth)
        {
            foreach (var s in level.OrderBy(s => s.Range.Start.Line))
            {
                var start = s.Range.Start.Line + 1;
                var end = s.Range.End.Line + 1;
                text.Append('\n').Append(' ', depth * 2).Append(end > start ? $"{start}-{end}" : $"{start}").Append(' ').Append(KindOf(s.Kind)).Append(' ').Append(s.Name);
                if (s.Detail is { Length: > 0 } detail && !s.Name.Contains(detail, StringComparison.Ordinal))
                    text.Append(" : ").Append(detail);
                Append(s.Children, depth + 1);
            }
        }
    }
}

/// <summary><c>code_read_symbol</c>: a declaration's source by its name.</summary>
sealed class CodeReadSymbolTool(MainViewModel vm) : CodeTool(vm)
{
    /// <summary>Declarations of one name a call gives the source of at most (overloads, partial types); the rest are listed.</summary>
    const int MaxShown = 5;

    public override string Name => "code_read_symbol";

    public override string Description =>
        "The source of a declaration by its name, with line numbers: a type, a method (all its overloads), a property, a field, a function, "
        + "from its doc comment and attributes to its end. Use it instead of reading a file by line ranges (sed -n, cat, open_document) when you know what you want to read. "
        + "Name a member as Type.Member; give path to look in one file only.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "symbol":{"type":"string","description":"The declaration's name: Type, Type.Member, Namespace.Type.Member, function."},
          "path":{"type":"string","description":"The file it is declared in, relative to the solution's folder (optional)."}},
         "required":["symbol"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Read Symbol", Str(input, "symbol"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol") ?? throw new InvalidOperationException("Give the name of the declaration to read.");
        var declarations = await DeclarationsAsync(symbol, Str(input, "path"), cancellationToken);
        if (declarations.Count == 0)
            return ClaudeToolOutput.Error($"No declaration is named {symbol}; code_symbols finds names that match.");
        var ordered = declarations.OrderBy(d => Relative(d.File), StringComparer.Ordinal).ThenBy(d => d.Symbol.Range.Start.Line).ToList();
        var text = new StringBuilder();
        FoundCode? first = null;
        foreach (var d in ordered.Take(MaxShown))
        {
            var (source, code) = Source(d.File, await LinesOfAsync(d.File, cancellationToken), d.Symbol, $"{KindOf(d.Symbol.Kind)} {d.QualifiedName}");
            text.Append(text.Length > 0 ? "\n\n" : "").Append(source);
            first ??= code;
        }
        if (ordered.Count > MaxShown)
            text.Append($"\n\n{ordered.Count - MaxShown} more (give the path to read one):\n")
                .Append(string.Join("\n", ordered.Skip(MaxShown).Select(d => $"{Relative(d.File)}:{d.Symbol.SelectionRange.Start.Line + 1}: {KindOf(d.Symbol.Kind)} {d.QualifiedName}")));
        return new ClaudeToolOutput(text.ToString()) { Detail = first };
    }
}

/// <summary>Input of the tools about a name where it is written: the name, and optionally where.</summary>
static class CodePositionSchema
{
    public const string Properties = """
          "symbol":{"type":"string","description":"The name as written in the code (Member, or Type.Member when it is not on a given line)."},
          "path":{"type":"string","description":"The file the name is written in (declared or used), relative to the solution's folder."},
          "line":{"type":"integer","description":"The line of that file it is on, from 1."},
          "column":{"type":"integer","description":"Its column on the line, from 1, when the name is on the line more than once."}
        """;
}

/// <summary><c>code_definition</c>: what a name written on a line refers to (Go to Definition), with its source.</summary>
sealed class CodeDefinitionTool(MainViewModel vm) : CodeTool(vm)
{
    const int MaxShown = 3;

    public override string Name => "code_definition";

    public override string Description =>
        "Go to Definition: what a name used on a line of a file refers to (a method called, a type, a property, a variable), with the declaration's source. "
        + "It finds the right overload or extension method, which grep cannot. Give the name, the file and the line it is on.";

    public override JsonObject InputSchema => Schema($$"""
        {"type":"object","properties":{ {{CodePositionSchema.Properties}} },"required":["symbol","path","line"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Go to Definition", Str(input, "symbol"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (lsp, file, position, name) = await PositionOfAsync(input, cancellationToken);
        var locations = await AskAboutAsync(lsp, file, () => lsp.DefinitionAsync(file, position, cancellationToken), cancellationToken);
        if (locations.Count == 0)
            return ClaudeToolOutput.Error($"The language server finds no definition of {name} there.");
        var text = new StringBuilder();
        FoundCode? first = null;
        foreach (var location in locations.Take(MaxShown))
        {
            text.Append(text.Length > 0 ? "\n\n" : "");
            if (SolutionFileOf(location) is not { } target)
            {
                // Not the solution's (a library's, decompiled): where it is, and its line.
                var at = location.FilePath;
                var line = File.Exists(at) ? (await File.ReadAllLinesAsync(at, cancellationToken)).ElementAtOrDefault(location.Range.Start.Line) : null;
                text.Append($"{at}:{location.Range.Start.Line + 1} (outside the solution){(line is null ? "" : ": " + Shown(line))}");
                continue;
            }
            var outline = await OutlineAsync(lsp, target, cancellationToken);
            var lines = await LinesOfAsync(target, cancellationToken);
            if (PathTo(outline, location.Range.Start) is { Count: > 0 } path)
            {
                var (source, code) = Source(target, lines, path[^1], $"{KindOf(path[^1].Kind)} {QualifiedName(path)}", 150);
                text.Append(source);
                first ??= code;
            }
            else
                text.Append($"{Relative(target)}:{location.Range.Start.Line + 1}: {Shown(lines[Math.Min(location.Range.Start.Line, lines.Length - 1)])}");
        }
        if (locations.Count > MaxShown)
            text.Append($"\n\n… and {locations.Count - MaxShown} more definitions.");
        return new ClaudeToolOutput(text.ToString()) { Detail = first };
    }
}

/// <summary>The uses of a name found by a server (references, implementations), by file, each with the member it is in.</summary>
abstract class CodeLocationsTool(MainViewModel vm) : CodeTool(vm)
{
    /// <summary>Files whose outline is asked for, to say which member each line is in; past them, only the lines.</summary>
    const int MaxOutlinedFiles = 40;

    protected async Task<ClaudeToolOutput> ListAsync(string heading, string name, IReadOnlyList<LspLocation> locations, IReadOnlyCollection<LspLocation> declarations,
        LspClient lsp, int limit, CancellationToken cancellationToken)
    {
        var inSolution = locations.Select(l => (File: SolutionFileOf(l), Location: l)).Where(l => l.File is not null).Select(l => (File: l.File!, l.Location))
            .DistinctBy(l => (l.File, l.Location.Range.Start))
            .OrderBy(l => Relative(l.File), StringComparer.Ordinal).ThenBy(l => l.Location.Range.Start.Line).ThenBy(l => l.Location.Range.Start.Character).ToList();
        if (inSolution.Count == 0)
            return new ClaudeToolOutput($"No {heading} of {name} in the solution.");
        var shown = inSolution.Take(limit).ToList();
        var files = shown.Select(l => l.File).Distinct().ToList();
        var text = new StringBuilder($"{inSolution.Count} {heading} of {name} in {inSolution.Select(l => l.File).Distinct().Count()} file(s):");
        var detail = new List<FoundText>();
        var outlined = 0;
        foreach (var file in files)
        {
            var lines = await LinesOfAsync(file, cancellationToken);
            IReadOnlyList<LspDocumentSymbol> outline = [];
            try
            {
                if (outlined++ < MaxOutlinedFiles)
                    outline = await OutlineAsync(lsp, file, cancellationToken);
            }
            catch (LspException)
            {
                // A file of another language's (a Razor component's): its lines without their members.
            }
            text.Append('\n').Append(Relative(file));
            foreach (var (_, location) in shown.Where(l => l.File == file))
            {
                var start = location.Range.Start;
                var line = Shown(lines[Math.Min(start.Line, lines.Length - 1)]);
                var declared = declarations.Any(d => d.FilePath == file && d.Range.Start == start);
                var member = MemberAt(outline, start);
                text.Append("\n  ").Append(start.Line + 1)
                    .Append(declared ? " (declaration)" : member is null ? "" : $" (in {member})")
                    .Append(": ").Append(line);
                detail.Add(new FoundText(file, start.Line + 1, start.Character + 1, line));
            }
        }
        if (inSolution.Count > limit)
            text.Append($"\n(only the first {limit})");
        return new ClaudeToolOutput(text.ToString()) { Detail = detail };
    }

    protected static JsonObject PositionSchema(string required) => Schema($$"""
        {"type":"object","properties":{ {{CodePositionSchema.Properties}},
          "limit":{"type":"integer","description":"At most this many (default 200)."} },
         "required":[{{required}}]}
        """);
}

/// <summary><c>code_references</c>: every use of a declaration (Find All References), with the member each is in.</summary>
sealed class CodeReferencesTool(MainViewModel vm) : CodeLocationsTool(vm)
{
    public override string Name => "code_references";

    public override string Description =>
        "Find All References: every use of a declaration across the solution, by file, each line with the member it is in: who calls a method, "
        + "reads or sets a property, uses a type. It is exact where grep is not (overloads, same names elsewhere, renamed imports). "
        + "Give the symbol (Type.Member), or the symbol with the path and line it is written on (declared or used).";

    public override JsonObject InputSchema => PositionSchema("\"symbol\"");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Find All References", Str(input, "symbol"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (lsp, file, position, name) = await PositionOfAsync(input, cancellationToken);
        var references = await AskAboutAsync(lsp, file, () => lsp.ReferencesAsync(file, position, cancellationToken), cancellationToken);
        var declarations = await AskAboutAsync(lsp, file, () => lsp.DefinitionAsync(file, position, cancellationToken), cancellationToken);
        return await ListAsync("reference(s)", name, references, [.. declarations], lsp, Int(input, "limit", 200, 1, 2000), cancellationToken);
    }
}

/// <summary><c>code_implementations</c>: what implements an interface or a member, or derives from a type (Go to Implementation).</summary>
sealed class CodeImplementationsTool(MainViewModel vm) : CodeLocationsTool(vm)
{
    public override string Name => "code_implementations";

    public override string Description =>
        "Go to Implementation: the types that implement an interface or derive from a class, or the members that implement or override an interface's, "
        + "an abstract or a virtual member, each with the member or type it is in. Give the symbol (Type.Member), or with the path and line it is written on.";

    public override JsonObject InputSchema => PositionSchema("\"symbol\"");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Go to Implementation", Str(input, "symbol"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var (lsp, file, position, name) = await PositionOfAsync(input, cancellationToken);
        var implementations = await AskAboutAsync(lsp, file, () => lsp.ImplementationAsync(file, position, cancellationToken), cancellationToken);
        return await ListAsync("implementation(s)", name, implementations, [], lsp, Int(input, "limit", 200, 1, 2000), cancellationToken);
    }
}
