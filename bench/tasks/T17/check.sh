#!/usr/bin/env bash
# Passes when hidden tests of GCC's and Clang's lines (put in after the session) and the parser's own tests pass.
set -uo pipefail
here=$(dirname "$0")
cp "$here/HiddenTests.cs.txt" tests/Karvon.Tests/BenchHiddenGccTests.cs
out=$(BENGINE_KEYRING=off KARVON_GH_PATH=off KARVON_KUBECTL_PATH=off KARVON_DOTNET_DIAGNOSTICS=off \
  dotnet test tests/Karvon.Tests --filter "FullyQualifiedName~Karvon.Tests.BenchHiddenGccTests|FullyQualifiedName~Karvon.Tests.BuildOutputParserTests" 2>&1)
rm -f tests/Karvon.Tests/BenchHiddenGccTests.cs
summary=$(grep -E "(Passed|Failed)! +- " <<<"$out" | tail -1)
failed=$(grep -oE "Failed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
passed=$(grep -oE "Passed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
if [ "${failed:-1}" = 0 ] && [ "${passed:-0}" -ge 6 ]; then
  echo "GCC's and Clang's lines are read, and the parser's tests pass ($passed)"
else
  echo "tests: ${summary:-the tests did not build: $(grep -m1 -oE '[^ /]+\.cs\([0-9,]+\): error [A-Z]+[0-9]+[^[]*' <<<"$out")}"; exit 1
fi
