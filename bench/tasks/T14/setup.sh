#!/usr/bin/env bash
# The mistake: compose.yaml gives the api STORE_ADDR, but it reads STORE_URL, so it exits at start and is restarted.
set -euo pipefail
sed -i 's/^      STORE_URL: /      STORE_ADDR: /' compose.yaml
grep -q '^      STORE_ADDR: http://store:9000$' compose.yaml
