#!/usr/bin/env bash
# Passes when the module builds, go vet is clean and its tests (at least the 9 it has) pass. Offline, local Go only.
set -uo pipefail
export GOTOOLCHAIN=local GOPROXY=off GOFLAGS=
fail() { echo "$1"; exit 1; }
first() { grep -m1 -E -e "$1" "$log" || tail -n 1 "$log"; }
log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT
go build ./... > "$log" 2>&1 || fail "go build fails: $(first '\.go:[0-9]+')"
go vet ./... > "$log" 2>&1 || fail "go vet fails: $(first '\.go:[0-9]+')"
go test -count=1 -v ./... > "$log" 2>&1 || fail "go test fails: $(first '--- FAIL|\.go:[0-9]+')"
passed=$(grep -c '^--- PASS: ' "$log")
[ "$passed" -ge 9 ] || fail "expected at least 9 tests to pass, $passed did"
echo "the module builds, go vet is clean and its $passed tests pass"
