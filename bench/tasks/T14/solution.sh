#!/usr/bin/env bash
set -euo pipefail
sed -i 's/^      STORE_ADDR: /      STORE_URL: /' compose.yaml
