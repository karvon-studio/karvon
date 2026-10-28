#!/usr/bin/env bash
# The key is in the system keyring (else for the session only), behind PortsSecrets (Get; Set and Forget), which
# PortsViewModel reads in KeyAsync/HasKey and writes from AskServiceAsync (the service dialog).
answer=$(cat "$BENCH_ANSWER")
has() { grep -qi -- "$1" <<<"$answer"; }
missing=()
has "keyring" || missing+=("the keyring")
has "PortsSecrets" || missing+=("PortsSecrets")
has "KeyAsync" || has "AskServiceAsync" || has "PortsViewModel" || missing+=("the view model's code that uses it")
if [ ${#missing[@]} -eq 0 ]; then echo "names the keyring, PortsSecrets and its callers"; else echo "missing: ${missing[*]}"; exit 1; fi
