#!/usr/bin/env bash
# Passes when pytest runs all 15 tests and they pass, with the test files as they were.
set -uo pipefail
fail() { echo "$1"; exit 1; }
for entry in \
  "tests/test_durations.py bfa44411fe761c186621c2192acdd7e11914f077422f873eda183aad9324b524" \
  "tests/test_ranges.py 740e33e9cb1224e8f77cb06e7eb9b9e113fddbd298e64dd00f3483b4725f3e81"; do
  file=${entry% *} want=${entry#* }
  [ -f "$file" ] || fail "$file is missing"
  [ "$(sha256sum "$file" | cut -d' ' -f1)" = "$want" ] || fail "$file was changed"
done
xml=$(mktemp) && log=$(mktemp) || fail "no temp file"
trap 'rm -f "$xml" "$log"' EXIT
python3 -m pytest -q -p no:cacheprovider --junitxml="$xml" > "$log" 2>&1
status=$?
[ -s "$xml" ] || fail "pytest gave no results (exit $status): $(tail -n 1 "$log")"
python3 - "$xml" <<'PY' || exit 1
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
suites = [root] if root.tag == "testsuite" else root.findall("testsuite")
count = {k: sum(int(s.get(k, 0)) for s in suites) for k in ("tests", "failures", "errors", "skipped")}
failed = [c.get("name") for s in suites for c in s.iter("testcase") if c.find("failure") is not None or c.find("error") is not None]
if count["failures"] or count["errors"]:
    print("tests fail: " + "; ".join(failed))
    sys.exit(1)
if count["tests"] != 15 or count["skipped"]:
    print(f"expected 15 tests to pass, {count['tests'] - count['skipped']} of {count['tests']} ran")
    sys.exit(1)
PY
[ "$status" -eq 0 ] || fail "pytest exits with $status"
echo "all 15 tests pass, test files unchanged"
