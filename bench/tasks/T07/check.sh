#!/usr/bin/env bash
# March 2026's best seller by units is Espresso Cup (98 units in 14 orders), bought by 11 customers. Not Coffee Filters
# (84 units, the best seller over the three months), House Blend Beans (most orders) or Hand Grinder (most revenue).
set -uo pipefail
[ -s "${BENCH_ANSWER:-}" ] || { echo "no answer"; exit 1; }
python3 - "$BENCH_ANSWER" <<'PY'
import re
import sys

text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
words = " ".join(re.sub(r"[^a-z0-9]+", " ", text.lower()).split())
product = re.search(r"\bespresso cups?\b", words) or re.search(r"\bec 090\b", words)
count = re.search(r"(?<![\d.,:/-])11(?!\d|[.,:/-]\d)", text) or re.search(r"\beleven\b", words)
if not product:
    print("the answer does not name Espresso Cup")
    sys.exit(1)
if not count:
    print("the answer does not give 11 customers")
    sys.exit(1)
print("names Espresso Cup and 11 customers")
PY
