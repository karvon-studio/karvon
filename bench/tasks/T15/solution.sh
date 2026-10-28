#!/usr/bin/env bash
set -euo pipefail
python3 - <<'PY'
p = "src/Karvon.Core/Build/BuildOutputParser.cs"
s = open(p).read()
bad = '''            hasLocation ? int.Parse(m.Groups["col"].Value) : 0,
            hasLocation ? int.Parse(m.Groups["line"].Value) : 0,'''
good = '''            hasLocation ? int.Parse(m.Groups["line"].Value) : 0,
            hasLocation ? int.Parse(m.Groups["col"].Value) : 0,'''
open(p, "w").write(s.replace(bad, good))
PY
