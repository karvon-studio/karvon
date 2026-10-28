#!/usr/bin/env bash
set -euo pipefail
sed -i 's/Lines.Sum(line => line.UnitPrice)/Lines.Sum(line => line.LineTotal)/' Shop.Core/Order.cs
