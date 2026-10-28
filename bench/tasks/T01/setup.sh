#!/usr/bin/env bash
# Three compile errors in three projects: a missing using (Components), a member under an old name (Shop.Api),
# an argument of the wrong type (Shop.Cli).
set -euo pipefail

replace() { # file old new: exactly one occurrence of old becomes new
    python3 - "$@" <<'PY'
import sys
path, old, new = sys.argv[1:4]
with open(path, encoding="utf-8", newline="") as f:
    text = f.read()
if text.count(old) != 1:
    sys.exit(f"{path}: expected one {old!r}, found {text.count(old)}")
with open(path, "w", encoding="utf-8", newline="") as f:
    f.write(text.replace(old, new))
PY
}

replace Components/Greeting.razor.cs $'using Microsoft.AspNetCore.Components;\n\n' ''
replace Shop.Api/OrderResponse.cs 'line.Quantity, line.LineTotal))' 'line.Quantity, line.Total))'
replace Shop.Cli/Program.cs 'Checkout.Run(cart);' 'Checkout.Run(cart.Lines);'
