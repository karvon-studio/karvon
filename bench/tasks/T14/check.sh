#!/usr/bin/env bash
# Passes when the stack comes up, the api answers 200 on port 8000 (/health, which reaches the store) within 40 s, and
# its container has not restarted and does not for 10 s after. On the run's own Docker daemon; the stack is removed.
set -uo pipefail
fail() { echo "$1"; exit 1; }
if docker compose version > /dev/null 2>&1; then
  compose() { docker compose -f compose.yaml "$@"; }
elif command -v docker-compose > /dev/null 2>&1; then
  compose() { docker-compose -f compose.yaml "$@"; }
else
  fail "neither docker compose nor docker-compose is installed"
fi
log=$(mktemp) || fail "no temp file"
trap 'compose down -v --remove-orphans > /dev/null 2>&1; rm -f "$log"' EXIT

health='import sys, urllib.request
try:
    sys.exit(0 if urllib.request.urlopen("http://127.0.0.1:8000/health", timeout=3).status == 200 else 1)
except Exception:
    sys.exit(1)'
# The published port, else the port inside the api's container.
probe() { python3 -c "$health" 2> /dev/null || docker exec "$cid" python -c "$health" > /dev/null 2>&1; }
state() { docker inspect -f '{{.State.Status}} {{.RestartCount}}' "$cid" 2> /dev/null || echo "gone"; }
last_log() { compose logs --no-color --tail=1 api 2> /dev/null | tail -n 1; }

compose up -d --build --force-recreate > "$log" 2>&1 \
  || fail "docker compose up fails: $(grep -m1 -i error "$log" || tail -n 1 "$log")"
cid=$( (compose ps -a -q api 2> /dev/null || compose ps -q api 2> /dev/null) | head -n 1)
[ -n "$cid" ] || fail "no api container after docker compose up"
deadline=$((SECONDS + 40))
until probe; do
  [ "$SECONDS" -lt "$deadline" ] || fail "the api does not answer 200 within 40 s ($(state); last log: $(last_log))"
  sleep 1
done
[ "$(state)" = "running 0" ] || fail "the api answers, but its container restarted ($(state))"
for _ in 1 2 3 4 5 6 7 8 9 10; do
  sleep 1
  [ "$(state)" = "running 0" ] || fail "the api container restarts after it answered ($(state); last log: $(last_log))"
done
echo "the api answers 200 on port 8000 and its container does not restart"
