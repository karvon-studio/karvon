using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;

namespace Karvon.Workspace;

/// <summary>
/// A tasks.json task's problemMatcher, as VS Code reads it: VS Code's own by their names ($msCompile, $tsc, $gcc, $go),
/// or one of the task's own, a pattern of one line (its regexp and which of its groups are the file, line, column,
/// severity, code and message) with where its files are (fileLocation). A pattern of several lines is not read yet.
/// </summary>
public static partial class ProblemMatchers
{
    [GeneratedRegex(@"^(.*?):(\d+):(\d*):?\s+(?:fatal\s+)?(warning|error):\s+(.*)$")]
    private static partial Regex Gcc();

    [GeneratedRegex(@"^([^:]*: )?((.:)?[^:]*):(\d+)(:(\d+))?: (.*)$")]
    private static partial Regex Go();

    /// <summary>How a task's output says its problems, or null when it has no matcher Karvon reads.</summary>
    /// <param name="matcher">The task's problemMatcher: a name, an object, or a list of them (the first that matches a line wins).</param>
    /// <param name="variables">The task's variables put in (fileLocation's ${workspaceFolder}).</param>
    public static Func<string, BuildDiagnostic?>? Of(JsonElement? matcher, Func<string, string> variables)
    {
        var matchers = matcher switch
        {
            { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Select(m => One(m, variables)).OfType<Func<string, BuildDiagnostic?>>().ToList(),
            { } one when One(one, variables) is { } read => [read],
            _ => [],
        };
        return matchers.Count switch
        {
            0 => null,
            1 => matchers[0],
            _ => line => matchers.Select(m => m(line)).FirstOrDefault(d => d is not null),
        };
    }

    static Func<string, BuildDiagnostic?>? One(JsonElement matcher, Func<string, string> variables) => matcher.ValueKind switch
    {
        JsonValueKind.String => Named(matcher.GetString()!),
        JsonValueKind.Object => Own(matcher, variables),
        _ => null,
    };

    /// <summary>VS Code's own matchers Karvon knows, by name.</summary>
    static Func<string, BuildDiagnostic?>? Named(string name) => name switch
    {
        "$msCompile" or "$tsc" or "$tsc-watch" => BuildOutputParser.Parse,
        "$gcc" => line => Gcc().Match(line) is { Success: true } m
            ? new BuildDiagnostic(SeverityOf(m.Groups[4].Value), "", m.Groups[5].Value, m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : 1, null)
            : null,
        "$go" => line => Go().Match(line) is { Success: true } m
            ? new BuildDiagnostic(DiagnosticSeverity.Error, "", m.Groups[7].Value, m.Groups[2].Value, int.Parse(m.Groups[4].Value),
                m.Groups[6].Value.Length > 0 ? int.Parse(m.Groups[6].Value) : 1, null)
            : null,
        _ => null,
    };

    /// <summary>A task's own matcher: { "pattern": { "regexp": ..., "file": 1, "line": 2, ... }, "severity", "fileLocation" }.</summary>
    static Func<string, BuildDiagnostic?>? Own(JsonElement matcher, Func<string, string> variables)
    {
        var basis = matcher.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? Named(b.GetString()!) : null;
        var pattern = matcher.TryGetProperty("pattern", out var p) ? p : default;
        if (pattern.ValueKind == JsonValueKind.Array)
            pattern = pattern.GetArrayLength() == 1 ? pattern[0] : default;
        if (pattern.ValueKind != JsonValueKind.Object || !pattern.TryGetProperty("regexp", out var r) || r.GetString() is not { Length: > 0 } regexp)
            return basis;
        Regex regex;
        try
        {
            regex = new Regex(regexp, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return basis;
        }
        int Group(string name, int fallback) => pattern.TryGetProperty(name, out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : fallback;
        var (file, line, column, severity, code, message, location) =
            (Group("file", 1), Group("line", 2), Group("column", 3), Group("severity", 0), Group("code", 0), Group("message", 0), Group("location", 0));
        // VS Code's defaults: the message is the 4th group after a location, else the 5th.
        if (message == 0)
            message = location > 0 ? 4 : 5;
        var defaultSeverity = matcher.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.String ? SeverityOf(s.GetString()!) : DiagnosticSeverity.Error;
        var under = FileLocation(matcher, variables);
        return text =>
        {
            Match m;
            try
            {
                m = regex.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
            if (!m.Success)
                return null;
            string Value(int group) => group > 0 && group < m.Groups.Count ? m.Groups[group].Value : "";
            int Number(int group) => int.TryParse(Value(group), out var n) ? n : 0;
            // "location": line, or line,column, or line,column,endLine,endColumn in one group (or with colons: 10:4).
            var (lineNumber, columnNumber) = location > 0 && Value(location).Split(',', ':') is [var l, .. var rest]
                ? (int.TryParse(l, out var ln) ? ln : 0, rest.Length > 0 && int.TryParse(rest[0], out var cn) ? cn : 1)
                : (Number(line), column > 0 && Value(column).Length > 0 ? Number(column) : 1);
            var path = Value(file);
            return new BuildDiagnostic(severity > 0 && Value(severity).Length > 0 ? SeverityOf(Value(severity)) : defaultSeverity, Value(code), Value(message),
                path.Length == 0 ? null : under is null ? path : Path.GetFullPath(path, under), lineNumber, columnNumber, null);
        };
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
