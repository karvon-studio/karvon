#!/usr/bin/env bash
# The bug: ranges that only touch are no longer merged, so one test fails.
set -euo pipefail
sed -i 's/if merged and start <= merged\[-1\]\[1\]:/if merged and start < merged[-1][1]:/' src/timespan/ranges.py
grep -q 'start < merged\[-1\]\[1\]' src/timespan/ranges.py
