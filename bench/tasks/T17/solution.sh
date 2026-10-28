#!/usr/bin/env bash
set -euo pipefail
python3 - <<'PY'
p = "src/Karvon.Core/Build/BuildOutputParser.cs"
s = open(p).read()
s = s.replace('''    public static BuildDiagnostic? Parse(string line)
    {''', '''    /// <summary>GCC's and Clang's "file:line:col: error: message".</summary>
    [GeneratedRegex(@"^\\s*(?<file>[^:\\s][^:]*):(?<line>\\d+):(?<col>\\d+):\\s*(?<sev>error|warning):\\s*(?<msg>.*?)\\s*$")]
    private static partial Regex Gcc();

    public static BuildDiagnostic? Parse(string line)
    {
        if (Gcc().Match(line) is { Success: true } gcc)
            return new BuildDiagnostic(gcc.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "",
                gcc.Groups["msg"].Value, gcc.Groups["file"].Value, int.Parse(gcc.Groups["line"].Value), int.Parse(gcc.Groups["col"].Value), null);''', 1)
open(p, "w").write(s)
PY
