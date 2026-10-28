#!/usr/bin/env bash
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

replace Components/Greeting.razor.cs 'namespace Components;' $'using Microsoft.AspNetCore.Components;\n\nnamespace Components;'
replace Shop.Api/OrderResponse.cs 'line.Quantity, line.Total))' 'line.Quantity, line.LineTotal))'
replace Shop.Cli/Program.cs 'Checkout.Run(cart.Lines);' 'Checkout.Run(cart);'
