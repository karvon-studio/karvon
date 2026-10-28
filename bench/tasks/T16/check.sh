#!/usr/bin/env bash
# Passes when the whole solution builds again and a few of its tests that touch the modules pass.
set -uo pipefail
build=$(dotnet build Karvon.slnx -nologo 2>&1)
if ! grep -qE "^Build succeeded|0 Error\(s\)" <<<"$build" || grep -qE " error [A-Z]+[0-9]+" <<<"$build"; then
  echo "the solution does not build: $(grep -cE ' error [A-Z]+[0-9]+' <<<"$build") error line(s), first: $(grep -m1 -oE '[^ /]+\.cs\([0-9,]+\): error [A-Z]+[0-9]+' <<<"$build")"; exit 1
fi
out=$(BENGINE_KEYRING=off KARVON_GH_PATH=off KARVON_KUBECTL_PATH=off KARVON_DOTNET_DIAGNOSTICS=off \
  dotnet test tests/Karvon.Tests --no-build --filter "FullyQualifiedName~Karvon.Tests.BuildOutputParserTests|FullyQualifiedName~Karvon.Tests.HotReloadTests.Dotnet_watchs_lines|FullyQualifiedName~Karvon.Tests.NodeHotReloadTests.The_agents_messages" 2>&1)
summary=$(grep -E "(Passed|Failed)! +- " <<<"$out" | tail -1)
failed=$(grep -oE "Failed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
passed=$(grep -oE "Passed: +[0-9]+" <<<"$summary" | grep -oE "[0-9]+")
if [ "${failed:-1}" = 0 ] && [ "${passed:-0}" -ge 5 ]; then
  echo "the solution builds and its tests pass ($passed)"
else
  echo "tests: ${summary:-no summary: $(tail -3 <<<"$out" | tr '\n' ' ')}"; exit 1
fi
