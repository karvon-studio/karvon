#!/usr/bin/env bash
# Passes when Vitest runs all 12 tests and they pass, with the test files as they were.
set -uo pipefail
fail() { echo "$1"; exit 1; }
for entry in \
  "src/cart.test.ts 23e7c1fba3c85e7798c2d4a7ed4e45e176d6ac8d311865f5595d0dd34714e4bc" \
  "src/money.test.ts 7a5df2d2cac2304b7735afb75691b21b95ae6619cfffa7f44599c3b3ef817a76"; do
  file=${entry% *} want=${entry#* }
  [ -f "$file" ] || fail "$file is missing"
  [ "$(sha256sum "$file" | cut -d' ' -f1)" = "$want" ] || fail "$file was changed"
done
report=$(mktemp) && log=$(mktemp) || fail "no temp file"
trap 'rm -f "$report" "$log"' EXIT
npx --no vitest run --reporter=json --outputFile="$report" > "$log" 2>&1
status=$?
[ -s "$report" ] || fail "vitest run gave no results (exit $status): $(grep -m1 -i 'error' "$log")"
node -e '
const r = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const failed = r.testResults.flatMap((f) => f.assertionResults).filter((t) => t.status !== "passed").map((t) => t.fullName);
if (r.numFailedTests > 0 || r.numFailedTestSuites > 0 || !r.success) {
  console.log(`tests fail: ${failed.join("; ") || "a test file does not run"}`); process.exit(1);
}
if (r.numPassedTests !== 12 || r.numTotalTests !== 12) {
  console.log(`expected 12 tests to pass, ${r.numPassedTests} of ${r.numTotalTests} did`); process.exit(1);
}' "$report" || exit 1
[ "$status" -eq 0 ] || fail "vitest run exits with $status"
echo "all 12 tests pass, test files unchanged"
