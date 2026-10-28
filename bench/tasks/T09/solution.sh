#!/usr/bin/env bash
set -euo pipefail
sed -i 's/if merged and start < merged\[-1\]\[1\]:/if merged and start <= merged[-1][1]:/' src/timespan/ranges.py
