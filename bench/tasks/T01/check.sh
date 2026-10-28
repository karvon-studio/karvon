#!/usr/bin/env bash
# Passes when Shop.sln builds with all its projects and the tests pass.
set -uo pipefail
fail() { echo "$1"; exit 1; }
log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT

first_error() { # the first compiler error of the log, without the workspace's path
    local line
    line=$(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log") || { echo "see dotnet build"; return; }
    line=${line//$PWD\//}
    echo "${line% \[*}"
}

first_failure() { # the first failed test of the log
    local line
    line=$(grep -m1 -E '^\s*Failed [A-Za-z_]' "$log") || { echo "see dotnet test"; return; }
    echo "${line#"${line%%[![:space:]]*}"}"
}

projects=$(dotnet sln Shop.sln list 2>/dev/null | tr '\\' '/')
for project in Shop.Core/Shop.Core.csproj Shop.Api/Shop.Api.csproj Shop.Cli/Shop.Cli.csproj \
    Web/Web.csproj Components/Components.csproj Tests/Tests.csproj; do
    grep -qxF "$project" <<<"$projects" || fail "$project is no longer in Shop.sln"
done

dotnet build Shop.sln >"$log" 2>&1 || fail "the build fails: $(first_error)"
timeout 120 dotnet test Shop.sln --no-build >"$log" 2>&1 || fail "the tests fail: $(first_failure)"
echo "Shop.sln builds and the tests pass"
