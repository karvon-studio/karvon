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

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ")]
    private static partial Regex GitHubTime();

    [GeneratedRegex(@"section_(start|end):\d+:[A-Za-z0-9_.\-]+(\[[^\]]*\])?\r?(\u001b\[0K)?")]
    private static partial Regex GitLabSection();

    [GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
