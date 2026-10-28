#!/usr/bin/env bash
# Passes when Shop.Api answers GET /api/orders/7 with the total 82.50 (42.00 + 2 x 7.25 + 18.50 + 3 x 2.50),
# and the solution builds and its tests pass.
set -uo pipefail
fail() { echo "$1"; exit 1; }
port=5291
expected=82.50

log=$(mktemp) && api_log=$(mktemp) || fail "no temp file"
pid=
cleanup() {
    if [ -n "$pid" ]; then
        kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
        for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
        kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
    fi
    rm -f "$log" "$api_log"
}
trap cleanup EXIT

dotnet build Shop.sln >"$log" 2>&1 \
    || fail "the build fails: $(grep -m1 -E ': error [A-Z]+[0-9]+:' "$log" | sed -E 's/ \[[^]]*\]$//')"
timeout 120 dotnet test Shop.sln --no-build >"$log" 2>&1 \
    || fail "the tests fail: $(grep -m1 -E '^\s*Failed [A-Za-z_]' "$log" | sed -E 's/^\s+//')"

python3 -c 'import socket, sys; s = socket.socket(); s.settimeout(1); sys.exit(s.connect_ex(("127.0.0.1", int(sys.argv[1]))) != 0)' "$port" \
    && fail "port $port is already in use"

# Its own process group, so the app dotnet run starts goes with it.
ASPNETCORE_URLS="http://127.0.0.1:$port" setsid dotnet run --project Shop.Api/Shop.Api.csproj --no-build --no-launch-profile \
    >"$api_log" 2>&1 &
pid=$!

get_order() { # 0: right total, 1: not answering yet, 2: answered wrong (says why)
    python3 - "$port" "$expected" <<'PY'
import json, sys, urllib.error, urllib.request
from decimal import Decimal
port, expected = sys.argv[1], Decimal(sys.argv[2])
try:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/orders/7", timeout=5) as response:
        body = response.read().decode("utf-8")
except urllib.error.HTTPError as e:
    print(f"GET /api/orders/7 answers HTTP {e.code}")
    sys.exit(2)
except (urllib.error.URLError, ConnectionError, TimeoutError):
    sys.exit(1)
try:
    order = json.loads(body, parse_float=Decimal)
    total = next(v for k, v in order.items() if k.lower() == "total")
    total = Decimal(str(total))
except Exception:
    print(f"GET /api/orders/7 has no total: {body[:200]}")
    sys.exit(2)
if total != expected:
    print(f"GET /api/orders/7 says the total is {total}, it is {expected}")
    sys.exit(2)
PY
}

for _ in $(seq 1 90); do
    answer=$(get_order); status=$?
    [ "$status" -eq 0 ] && { echo "GET /api/orders/7 has the total $expected, and the solution builds and its tests pass"; exit 0; }
    [ "$status" -eq 2 ] && fail "$answer"
    kill -0 "$pid" 2>/dev/null || fail "Shop.Api exited: $(grep -m1 -iE 'fail|error|exception' "$api_log" || tail -n1 "$api_log")"
    sleep 0.5
done
fail "Shop.Api did not answer on port $port within 45 seconds"
