#!/usr/bin/env bash
# What a session costs before any work: it only has to answer.
if grep -qiw "ok" "$BENCH_ANSWER"; then echo "answered OK"; else echo "no OK in the answer"; exit 1; fi
