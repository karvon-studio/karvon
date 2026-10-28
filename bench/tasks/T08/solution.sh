#!/usr/bin/env bash
set -euo pipefail
sed -i 's/return discounted + shippingCents(subtotal);/return discounted + shippingCents(discounted);/' src/cart.ts
