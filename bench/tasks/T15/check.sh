#!/usr/bin/env bash
# Passes when the parser's tests and the folder builds' pass again (15 tests), and no test file was changed.
set -uo pipefail
expected=29ba7a4bdbeb8c30bb77f08182ddde869da52e3fcae0a4135792e29a4f03cdfe
actual=$(find tests/Karvon.Tests -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -d' ' -f1)
if [ "$actual" != "$expected" ]; then echo "a test file was changed"; exit 1; fi
out=$(BENGINE_KEYRING=off KARVON_GH_PATH=off KARVON_KUBECTL_PATH=off KARVON_DOTNET_DIAGNOSTICS=off \
  dotnet test tests/Karvon.Tests --filter "FullyQualifiedName~Karvon.Tests.BuildOutputParserTests|FullyQualifiedName~Karvon.Tests.WorkspaceTaskTests" 2>&1)
summary=$(grep -E "(Passed|Failed)! +- " <<<"$out" | tail -1)
failed=$(grep -oE "Failed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
passed=$(grep -oE "Passed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
if [ "${failed:-1}" = 0 ] && [ "${passed:-0}" = 15 ]; then
  echo "the 15 tests pass, and no test file was changed"
else
  echo "tests: ${summary:-no summary: $(tail -3 <<<"$out" | tr '\n' ' ')}"; exit 1
fi
