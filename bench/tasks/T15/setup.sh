#!/usr/bin/env bash
# The bug: MSBuild's "file(line,col): error ..." is read with its line and column the other way round, so the Error List
# (and what the tasks' builds report) points at the wrong line. Three tests fail: the parser's own, and two folder
# builds whose errors are checked.
set -euo pipefail
python3 - <<'PY'
p = "src/Karvon.Core/Build/BuildOutputParser.cs"
s = open(p).read()
old = '''            hasLocation ? int.Parse(m.Groups["line"].Value) : 0,
            hasLocation ? int.Parse(m.Groups["col"].Value) : 0,'''
new = '''            hasLocation ? int.Parse(m.Groups["col"].Value) : 0,
            hasLocation ? int.Parse(m.Groups["line"].Value) : 0,'''
assert s.count(old) == 1
open(p, "w").write(s.replace(old, new))
PY
