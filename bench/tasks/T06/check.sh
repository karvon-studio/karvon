#!/usr/bin/env bash
# Passes when Shop.Cli prices its cart (subtotal 134.90, 10% off) with the discount 13.49 and the total 121.41,
# and the solution builds and its tests pass.
set -uo pipefail
fail() { echo "$1"; exit 1; }
log=$(mktemp) || fail "no temp file"
trap 'rm -f "$log"' EXIT

dotnet build Shop.sln >"$log" 2>&1 \
    || fail "the build fails: $(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log" | sed -E 's/ \[[^]]*\]$//')"
timeout 120 dotnet test Shop.sln --no-build >"$log" 2>&1 \
    || fail "the tests fail: $(grep -m1 -E '^\s*Failed [A-Za-z_]' "$log" | sed -E 's/^\s+//')"
timeout 60 dotnet run --project Shop.Cli/Shop.Cli.csproj --no-build </dev/null >"$log" 2>&1 \
    || fail "Shop.Cli fails: $(tail -n1 "$log")"
line=$(grep -m1 -E '^subtotal=' "$log") || fail "Shop.Cli prints no subtotal=... discount=... total=... line"
python3 - "$line" <<'PY' || exit 1
import re, sys
from decimal import Decimal
values = dict(re.findall(r"(\w+)=(-?[0-9.]+)", sys.argv[1]))
try:
    discount, total = Decimal(values["discount"]), Decimal(values["total"])
except (KeyError, ArithmeticError):
    print(f"Shop.Cli prints {sys.argv[1]!r}")
    sys.exit(1)
if discount < 0:
    print(f"the discount is still negative: {discount}")
    sys.exit(1)
if discount != Decimal("13.49") or total != Decimal("121.41"):
    print(f"Shop.Cli prints discount {discount} and total {total}, they are 13.49 and 121.41")
    sys.exit(1)
PY
echo "Shop.Cli prints the discount 13.49 and the total 121.41, and the solution builds and its tests pass"
