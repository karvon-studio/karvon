#!/usr/bin/env bash
cat > "$BENCH_ANSWER" <<'ANSWER'
The tunnel service's API key is kept in the system keyring (key "karvon-ports/<service>"), or for the session only, by
PortsSecrets in src/Karvon.Ports/PortsService.cs: Get reads it, Set and Forget write and remove it. PortsViewModel reads
it in KeyAsync and HasKey, and writes it from AskServiceAsync, the service dialog.
ANSWER
