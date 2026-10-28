#!/usr/bin/env bash
# Passes when cargo test runs all 8 tests and they pass, with the tests as they were.
set -uo pipefail
fail() { echo "$1"; exit 1; }
for entry in \
  "tests/parse.rs ef8eb135d3a7f418f24518144daad623d5acfb694bf0cea094c8cafc9aeb3520" \
  "tests/version.rs 53f1ca770c988aab5294c14dcf95b8e8b4f402a569b51440af3f499ce89fb925"; do
  file=${entry% *} want=${entry#* }
  [ -f "$file" ] || fail "$file is missing"
  [ "$(sha256sum "$file" | cut -d' ' -f1)" = "$want" ] || fail "$file was changed"
done
log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT
CARGO_TERM_COLOR=never cargo test --offline > "$log" 2>&1
status=$?
if [ "$status" -ne 0 ]; then
  failed=$(sed -n -E 's/^test (.*) \.\.\. FAILED$/\1/p' "$log" | paste -sd ' ' -)
  fail "cargo test fails: ${failed:-$(grep -m1 -E '^error' "$log" || tail -n 1 "$log")}"
fi
read -r passed ignored < <(awk '/^test result:/ { for (i = 1; i < NF; i++) { if ($(i + 1) == "passed;") p += $i; if ($(i + 1) == "ignored;") g += $i } } END { print p + 0, g + 0 }' "$log")
[ "$passed" -eq 8 ] && [ "$ignored" -eq 0 ] || fail "expected 8 tests to pass, $passed did ($ignored ignored)"
echo "all 8 tests pass, tests unchanged"
