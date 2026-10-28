#!/usr/bin/env bash
# The bug: an order's total adds up the unit prices, not the line totals, so two tests fail.
set -euo pipefail
sed -i 's/Lines.Sum(line => line.LineTotal)/Lines.Sum(line => line.UnitPrice)/' Shop.Core/Order.cs
grep -q 'Lines.Sum(line => line.UnitPrice)' Shop.Core/Order.cs
