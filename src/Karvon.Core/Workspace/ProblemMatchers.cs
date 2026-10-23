using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;

namespace Karvon.Workspace;

/// <summary>
/// A tasks.json task's problemMatcher, as VS Code reads it: VS Code's own by their names ($msCompile, $tsc, $gcc, $go,
/// $eslint-stylish, $eslint-compact), or one of the task's own: its pattern (its regexp and which of its groups are the
/// file, line, column, severity, code and message), or patterns of lines one after another (the last may loop: each
/// line after them it matches is one more problem in the same file), with where its files are (fileLocation).
/// </summary>
public static partial class ProblemMatchers
{
    [GeneratedRegex(@"^(.*?):(\d+):(\d*):?\s+(?:fatal\s+)?(warning|error):\s+(.*)$")]
    private static partial Regex Gcc();

    [GeneratedRegex(@"^([^:]*: )?((.:)?[^:]*):(\d+)(:(\d+))?: (.*)$")]
    private static partial Regex Go();

    [GeneratedRegex(@"^((?:[a-zA-Z]:)*[./\\]+.*?)$")]
    private static partial Regex EslintStylishFile();

    [GeneratedRegex(@"^\s+(\d+):(\d+)\s+(error|warning|info)\s+(.+?)(?:\s\s+(.*))?$")]
    private static partial Regex EslintStylishProblem();

    [GeneratedRegex(@"^(.+):\sline\s(\d+),\scol\s(\d+),\s(Error|Warning|Info)\s-\s(.+)\s\((.+)\)$")]
    private static partial Regex EslintCompact();

    /// <summary>One line's regexp and its groups: null where it does not say that (another line may).</summary>
    sealed record Pattern(Regex Regex, int? File = null, int? Line = null, int? Column = null, int? Location = null,
        int? Severity = null, int? Code = null, int? Message = null, bool Loop = false);

    /// <summary>
    /// How a task's output says its problems, or null when it has no matcher Karvon reads: a new reader for each run (one
    /// of several lines keeps its place between them).
    /// </summary>
    /// <param name="matcher">The task's problemMatcher: a name, an object, or a list of them (the first that matches a line wins).</param>
    /// <param name="variables">The task's variables put in (fileLocation's ${workspaceFolder}).</param>
    public static Func<Func<string, BuildDiagnostic?>>? Of(JsonElement? matcher, Func<string, string> variables)
    {
        var matchers = matcher switch
        {
            { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Select(m => One(m, variables)).OfType<Func<Func<string, BuildDiagnostic?>>>().ToList(),
            { } one when One(one, variables) is { } read => [read],
            _ => [],
        };
        return matchers.Count switch
        {
            0 => null,
            1 => matchers[0],
            // Each reads every line, so one of several lines keeps its place while another matches.
            _ => () =>
            {
                var readers = matchers.Select(m => m()).ToList();
                return line => readers.Select(r => r(line)).ToList().FirstOrDefault(d => d is not null);
            },
        };
    }

    static Func<Func<string, BuildDiagnostic?>>? One(JsonElement matcher, Func<string, string> variables) => matcher.ValueKind switch
    {
        JsonValueKind.String => Named(matcher.GetString()!),
        JsonValueKind.Object => Own(matcher, variables),
        _ => null,
    };

    /// <summary>VS Code's own matchers Karvon knows, by name.</summary>
    static Func<Func<string, BuildDiagnostic?>>? Named(string name) => name switch
    {
        "$msCompile" or "$tsc" or "$tsc-watch" => () => BuildOutputParser.Parse,
        "$gcc" => Reader([new(Gcc(), File: 1, Line: 2, Column: 3, Severity: 4, Message: 5)], DiagnosticSeverity.Error, null),
        "$go" => Reader([new(Go(), File: 2, Line: 4, Column: 6, Message: 7)], DiagnosticSeverity.Error, null),
        "$eslint-stylish" => Reader([new(EslintStylishFile(), File: 1),
            new(EslintStylishProblem(), Line: 1, Column: 2, Severity: 3, Message: 4, Code: 5, Loop: true)], DiagnosticSeverity.Error, null),
        "$eslint-compact" => Reader([new(EslintCompact(), File: 1, Line: 2, Column: 3, Severity: 4, Message: 5, Code: 6)], DiagnosticSeverity.Error, null),
        _ => null,
    };

    /// <summary>A task's own matcher: { "pattern": { "regexp": ..., "file": 1, "line": 2, ... } or a list of them, "severity", "fileLocation" }.</summary>
    static Func<Func<string, BuildDiagnostic?>>? Own(JsonElement matcher, Func<string, string> variables)
    {
        var basis = matcher.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? Named(b.GetString()!) : null;
        if (!matcher.TryGetProperty("pattern", out var p))
            return basis;
        var lines = p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().ToList() : [p];
        List<Pattern> patterns = [];
        foreach (var line in lines)
        {
            if (PatternOf(line, defaults: lines.Count == 1) is not { } pattern)
                return basis;
            patterns.Add(pattern);
        }
        if (patterns.Count == 0)
            return basis;
        var severity = matcher.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.String ? SeverityOf(s.GetString()!) : DiagnosticSeverity.Error;
        return Reader(patterns, severity, FileLocation(matcher, variables));
    }

    /// <summary>
    /// A pattern's regexp and groups; one alone has VS Code's defaults: the file, line and column are the first three
    /// groups, the message the 4th after a location, else the 5th.
    /// </summary>
    static Pattern? PatternOf(JsonElement pattern, bool defaults)
    {
        if (pattern.ValueKind != JsonValueKind.Object || !pattern.TryGetProperty("regexp", out var r) || r.GetString() is not { Length: > 0 } regexp)
            return null;
        Regex regex;
        try
        {
            regex = new Regex(regexp, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
        int? Group(string name) => pattern.TryGetProperty(name, out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : null;
        var read = new Pattern(regex, Group("file"), Group("line"), Group("column"), Group("location"), Group("severity"), Group("code"), Group("message"),
            pattern.TryGetProperty("loop", out var loop) && loop.ValueKind == JsonValueKind.True);
        if (!defaults)
            return read;
        var located = read.Location is not null;
        return read with
        {
            File = read.File ?? 1,
            Line = located ? read.Line : read.Line ?? 2,
            Column = located ? read.Column : read.Column ?? 3,
            Message = read.Message is null or 0 ? located ? 4 : 5 : read.Message,
        };
    }

    /// <summary>
    /// A reader of patterns on lines one after another: each line's groups fill what the lines before it did not say,
    /// and the last one's match is the problem. A last pattern that loops reads the lines after it too, a problem each,
    /// until one does not match it.
    /// </summary>
    static Func<Func<string, BuildDiagnostic?>> Reader(IReadOnlyList<Pattern> patterns, DiagnosticSeverity severity, string? under) => () =>
    {
        var next = 0;
        var read = new Fields();
        var looping = false;

        BuildDiagnostic? Matched(Match m)
        {
            var pattern = patterns[next];
            if (next == patterns.Count - 1 && pattern.Loop)
            {
                // What the lines before it said stays for each line it matches.
                looping = true;
                next = 0;
                return Diagnostic(read.With(pattern, m), severity, under);
            }
            read = read.With(pattern, m);
            if (++next < patterns.Count)
                return null;
            next = 0;
            return Diagnostic(read, severity, under);
        }

        return text =>
        {
            if (looping)
            {
                if (Match(patterns[^1], text) is { } more)
                    return Diagnostic(read.With(patterns[^1], more), severity, under);
                looping = false;
            }
            if (next > 0 && Match(patterns[next], text) is { } m)
                return Matched(m);
            next = 0;
            if (Match(patterns[0], text) is not { } first)
                return null;
            read = new Fields();
            return Matched(first);
        };
    };

    static Match? Match(Pattern pattern, string text)
    {
        try
        {
            return pattern.Regex.Match(text) is { Success: true } m ? m : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>What a problem's lines said so far: the first line to say one keeps it, as VS Code has it.</summary>
    sealed record Fields(string? File = null, string? Line = null, string? Column = null, string? Location = null, string? Severity = null,
        string? Code = null, string? Message = null)
    {
        public Fields With(Pattern pattern, Match m)
        {
            string? Value(int? group) => group is { } g && g >= 0 && g < m.Groups.Count && m.Groups[g].Success ? m.Groups[g].Value : null;
            return new Fields(File ?? Value(pattern.File), Line ?? Value(pattern.Line), Column ?? Value(pattern.Column), Location ?? Value(pattern.Location),
                Severity ?? Value(pattern.Severity), Code ?? Value(pattern.Code), Message ?? Value(pattern.Message));
        }
    }

    static BuildDiagnostic Diagnostic(Fields read, DiagnosticSeverity severity, string? under)
    {
        static int Number(string? text) => int.TryParse(text, out var n) ? n : 0;
        // "location": line, or line,column, or line,column,endLine,endColumn in one group (or with colons: 10:4).
        var (line, column) = read.Location?.Split(',', ':') is [var l, .. var rest]
            ? (Number(l), rest.Length > 0 && int.TryParse(rest[0], out var c) ? c : 1)
            : (Number(read.Line), read.Column is { Length: > 0 } col ? Number(col) : 1);
        var path = read.File ?? "";
        return new BuildDiagnostic(read.Severity is { Length: > 0 } said ? SeverityOf(said) : severity, read.Code ?? "", read.Message ?? "",
            path.Length == 0 ? null : under is null || !Path.IsPathFullyQualified(under) ? path : Path.GetFullPath(path, under), line, column, null);
    }

    /// <summary>Where a matcher's relative files are: ["relative", "${workspaceFolder}/src"]; null, where the task runs.</summary>
    static string? FileLocation(JsonElement matcher, Func<string, string> variables) =>
        matcher.TryGetProperty("fileLocation", out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() == 2
            && f[0].GetString() is "relative" or "autoDetect" && f[1].GetString() is { Length: > 0 } dir
            ? variables(dir)
            : null;

    static DiagnosticSeverity SeverityOf(string text) => text.ToLowerInvariant() switch
    {
        "warning" or "warn" => DiagnosticSeverity.Warning,
        "info" or "information" or "note" => DiagnosticSeverity.Information,
        "hint" => DiagnosticSeverity.Hint,
        _ => DiagnosticSeverity.Error,
    };
}
