using System.Text;
using System.Text.RegularExpressions;

namespace Karvon.Pipelines;

/// <summary>
/// A job's log as it reads: GitHub's timestamps and workflow commands (##[group], ##[error]) made plain and colored,
/// GitLab's section markers taken out (their ANSI colors stay; the Output colorizer shows them).
/// </summary>
public static partial class PipelineLog
{
    const string Bold = "\u001b[1m", Red = "\u001b[31m", Yellow = "\u001b[33m", Cyan = "\u001b[36m", Gray = "\u001b[90m", Reset = "\u001b[0m";

    public static string Clean(string log, CiProvider provider)
    {
        log = log.Replace("\r\n", "\n");
        if (provider == CiProvider.GitLab)
            return GitLabSection().Replace(log, "").Replace("\u001b[0K", "").Replace("\r", "");
        var text = new StringBuilder(log.Length);
        foreach (var raw in log.Split('\n'))
        {
            var line = GitHubTime().Replace(raw, "", 1).TrimStart('\uFEFF');
            if (line.StartsWith("##[endgroup]", StringComparison.Ordinal))
                continue;
            text.Append(line switch
            {
                _ when line.StartsWith("##[group]", StringComparison.Ordinal) => $"{Bold}▸ {line[9..]}{Reset}",
                _ when line.StartsWith("##[error]", StringComparison.Ordinal) => $"{Red}Error: {line[9..]}{Reset}",
                _ when line.StartsWith("##[warning]", StringComparison.Ordinal) => $"{Yellow}Warning: {line[11..]}{Reset}",
                _ when line.StartsWith("##[notice]", StringComparison.Ordinal) => $"{Cyan}Notice: {line[10..]}{Reset}",
                _ when line.StartsWith("##[command]", StringComparison.Ordinal) => $"{Cyan}{line[11..]}{Reset}",
                _ when line.StartsWith("##[debug]", StringComparison.Ordinal) => $"{Gray}{line[9..]}{Reset}",
                _ => line,
            }).Append('\n');
        }
        return text.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The log as Claude reads it: plain (no colors), the last <paramref name="lines"/> lines.</summary>
    public static string Plain(string cleaned, int lines)
    {
        var all = Ansi().Replace(cleaned, "").TrimEnd('\n').Split('\n');
        return all.Length <= lines ? string.Join('\n', all) : $"(the last {lines} of {all.Length} lines)\n" + string.Join('\n', all[^lines..]);
    }

    /// <summary>The log Claude reads, whole and plain.</summary>
    public static string[] PlainLines(string cleaned) => Ansi().Replace(cleaned, "").TrimEnd('\n').Split('\n');

    /// <summary>
    /// A GitHub job log's lines within the spans (a step's start to its end), by each line's timestamp; a line without
    /// one goes with the line before it. Its steps are not marked in the log, their times are.
    /// </summary>
    public static string Slice(string log, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> spans)
    {
        var text = new StringBuilder();
        var keep = false;
        foreach (var raw in log.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart('\uFEFF');
            if (GitHubTime().Match(line) is { Success: true } time
                && DateTimeOffset.TryParse(time.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                keep = spans.Any(s => at >= s.From.AddSeconds(-1) && at < s.To.AddSeconds(1));
            if (keep)
                text.Append(line).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The lines that match <paramref name="pattern"/> (a regular expression, or the text as it is when it is not one;
    /// case aside), each with <paramref name="context"/> lines around it and its line number; at most
    /// <paramref name="max"/> lines.
    /// </summary>
    public static string Grep(IReadOnlyList<string> lines, string pattern, int context, int max)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        var shown = new bool[lines.Count];
        var hits = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            bool match;
            try
            {
                match = regex.IsMatch(lines[i]);
            }
            catch (RegexMatchTimeoutException)
            {
                match = false;
            }
            if (!match)
                continue;
            hits++;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                shown[j] = true;
        }
        if (hits == 0)
            return $"No line of the {lines.Count} matches {pattern}.";
        var text = new StringBuilder($"{hits} of {lines.Count} lines match {pattern}:\n");
        var written = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!shown[i])
                continue;
            if (i > 0 && !shown[i - 1] && written > 0)
                text.Append("--\n");
            if (written++ == max)
            {
                text.Append($"… (more: narrow the pattern, or lower context)\n");
                break;
            }
            text.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ")]
    private static partial Regex GitHubTime();

    [GeneratedRegex(@"section_(start|end):\d+:[A-Za-z0-9_.\-]+(\[[^\]]*\])?\r?(\u001b\[0K)?")]
    private static partial Regex GitLabSection();

    [GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
