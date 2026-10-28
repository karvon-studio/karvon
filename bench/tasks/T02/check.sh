#!/usr/bin/env bash
# Passes when all 13 tests run and pass, with the test project's files as they were.
set -uo pipefail
fail() { echo "$1"; exit 1; }

expected="\
1137a357e69b7c5f44404c9bb4831f0bc30a9dff2f2bf30b8db8f33ae529243f  Tests/CheckoutTests.cs
8925d19c4bb60c61e270a8befde187d8ab886b54e0e4adcec4a4eb1dd82df7b6  Tests/GreetingTests.cs
4bc7c73ac3f468a2396bb60c1d836409a7bf9fa3adb5cf567d394c25bc933154  Tests/OrderTests.cs
b05300fc912994c09cf013ba1fec163a6bb3c597321325cf1c75c7dcd44d0b98  Tests/PriceFormatterTests.cs
9eb85cd475f05aecce8660d2ab7232449515fde5ebef07de9f7ab88a399a278e  Tests/Tests.csproj"
while read -r want file; do
    [ -f "$file" ] || fail "$file is missing"
    [ "$(sha256sum "$file" | cut -d' ' -f1)" = "$want" ] || fail "$file was changed"
done <<<"$expected"
files=$(find Tests -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) \
    -not -path 'Tests/bin/*' -not -path 'Tests/obj/*' | LC_ALL=C sort)
extra=$(comm -23 <(echo "$files") <(cut -d' ' -f3 <<<"$expected" | LC_ALL=C sort) | tr '\n' ' ')
[ -z "$extra" ] || fail "files were added to the test project: ${extra% }"

log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT
dotnet build Shop.sln >"$log" 2>&1 \
    || fail "the build fails: $(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log" | sed -E 's/ \[[^]]*\]$//')"
timeout 120 dotnet test Shop.sln --no-build >"$log" 2>&1
status=$?
summary=$(grep -m1 -E 'Failed: +[0-9]+, Passed: +[0-9]+' "$log") || fail "dotnet test gave no results (exit $status)"
read -r failed passed total < <(sed -E 's/.*Failed: +([0-9]+), Passed: +([0-9]+), Skipped: +[0-9]+, Total: +([0-9]+).*/\1 \2 \3/' <<<"$summary")
if [ "$status" -ne 0 ] || [ "$failed" -ne 0 ]; then
    names=$(grep -E '^\s*Failed [A-Za-z_]' "$log" | sed -E 's/^\s*Failed ([^ ]+).*/\1/' | tr '\n' ' ')
    fail "$failed of $total tests fail: ${names% }"
fi
[ "$passed" -eq 13 ] && [ "$total" -eq 13 ] || fail "expected 13 tests to pass, $passed of $total did"
echo "all 13 tests pass, test files unchanged"
