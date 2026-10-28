#!/usr/bin/env bash
# Two breaks: LowStock is renamed to BelowThreshold without its callers, and Value multiplies an int by an int64.
set -euo pipefail
sed -i 's/LowStock/BelowThreshold/g' stock/query.go
sed -i 's/total += int64(item.Quantity) \* item.UnitCents/total += item.Quantity * item.UnitCents/' stock/stock.go
grep -q 'func (s \*Store) BelowThreshold(' stock/query.go
grep -q 'total += item.Quantity \* item.UnitCents' stock/stock.go
