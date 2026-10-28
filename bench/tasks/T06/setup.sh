#!/usr/bin/env bash
# The bug: the discount subtracts the subtotal from the discounted price, so it is negative whenever there is one.
# The line numbers stay the same (the breakpoint is on line 19).
set -euo pipefail
sed -i 's/decimal discount = subtotal - discounted;/decimal discount = discounted - subtotal;/' Shop.Core/Checkout.cs
grep -q 'decimal discount = discounted - subtotal;' Shop.Core/Checkout.cs
