#!/usr/bin/env bash
# Passes when CONTRIBUTING.md is at the root, has at least 400 characters, names `dotnet build` and `dotnet test`,
# and the solution still builds.
set -uo pipefail
fail() { echo "$1"; exit 1; }

[ -f CONTRIBUTING.md ] || fail "there is no CONTRIBUTING.md at the root"
length=$(python3 -c 'import sys; print(len(open(sys.argv[1], encoding="utf-8", errors="replace").read()))' CONTRIBUTING.md)
[ "$length" -ge 400 ] || fail "CONTRIBUTING.md has $length characters, fewer than 400"
grep -qF 'dotnet build' CONTRIBUTING.md || fail "CONTRIBUTING.md does not mention dotnet build"
grep -qF 'dotnet test' CONTRIBUTING.md || fail "CONTRIBUTING.md does not mention dotnet test"

log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT
dotnet build Shop.sln >"$log" 2>&1 \
    || fail "the build fails: $(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log" | sed -E 's/ \[[^]]*\]$//')"
echo "CONTRIBUTING.md has $length characters with dotnet build and dotnet test, and the solution builds"
