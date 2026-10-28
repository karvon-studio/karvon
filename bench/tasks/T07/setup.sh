#!/usr/bin/env bash
# The answer is in the data alone: the generator stays out of the workspace.
set -euo pipefail
[ -f shop.db ] || python3 make-db.py
rm -f make-db.py
