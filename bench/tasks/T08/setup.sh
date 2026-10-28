#!/usr/bin/env bash
# The bug: shipping is decided on the subtotal before the discount, so one test fails.
set -euo pipefail
sed -i 's/return discounted + shippingCents(discounted);/return discounted + shippingCents(subtotal);/' src/cart.ts
grep -q 'shippingCents(subtotal)' src/cart.ts
