#!/usr/bin/env bash
# The bug: a line added to an order later adds its unit price to the kept total, not its line total.
# Order 7 is the only order with such a line of more than one item.
set -euo pipefail
sed -i 's/totals\[id\] += line.LineTotal;/totals[id] += line.UnitPrice;/' Shop.Api/OrderStore.cs
grep -q 'totals\[id\] += line.UnitPrice;' Shop.Api/OrderStore.cs
