#!/usr/bin/env bash
set -euo pipefail
sed -i 's/BelowThreshold/LowStock/g' stock/query.go
sed -i 's/total += item.Quantity \* item.UnitCents/total += int64(item.Quantity) * item.UnitCents/' stock/stock.go
