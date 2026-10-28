#!/usr/bin/env bash
set -euo pipefail
sed -i 's/totals\[id\] += line.UnitPrice;/totals[id] += line.LineTotal;/' Shop.Api/OrderStore.cs
