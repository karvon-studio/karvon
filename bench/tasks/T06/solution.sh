#!/usr/bin/env bash
set -euo pipefail
sed -i 's/decimal discount = discounted - subtotal;/decimal discount = subtotal - discounted;/' Shop.Core/Checkout.cs
