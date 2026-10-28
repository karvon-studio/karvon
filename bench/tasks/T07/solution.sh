#!/usr/bin/env bash
set -euo pipefail
python3 - > "$BENCH_ANSWER" <<'PY'
import sqlite3

db = sqlite3.connect("file:shop.db?mode=ro", uri=True)
name, units, buyers = db.execute("""
    SELECT p.name, SUM(i.quantity), COUNT(DISTINCT o.customer_id)
    FROM order_items i JOIN orders o ON o.id = i.order_id JOIN products p ON p.id = i.product_id
    WHERE o.placed_at >= '2026-03-01' AND o.placed_at < '2026-04-01'
    GROUP BY p.id ORDER BY SUM(i.quantity) DESC LIMIT 1""").fetchone()
print(f"{name} sold the most in March 2026 ({units} units); {buyers} customers bought it.")
PY
