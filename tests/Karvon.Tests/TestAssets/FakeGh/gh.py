#!/usr/bin/env python3
"""A fake gh CLI for the Pipelines and Pull Requests tests: `auth status` and `api`, answered from FAKE_GH_DATA/routes.json,
calls logged (a body given by --input -, on a line of its own after its call, as "< {json}")."""
import json
import os
import sys

DATA = os.environ["FAKE_GH_DATA"]
args = sys.argv[1:]
with open(os.path.join(DATA, "calls.txt"), "a") as log:
    log.write(" ".join(args) + "\n")
with open(os.path.join(DATA, "routes.json")) as f:
    routes = json.load(f)

if args[:2] == ["auth", "status"]:
    sys.exit(0 if routes.get("signed_in") else 1)

if args[:1] == ["api"]:
    method, path, i = "GET", None, 1
    while i < len(args):
        if args[i] in ("--hostname", "-H", "--header"):
            i += 2
            continue
        if args[i] == "--input":
            body = sys.stdin.read()
            with open(os.path.join(DATA, "calls.txt"), "a") as log:
                log.write("< " + json.dumps(json.loads(body), separators=(",", ":"), sort_keys=True) + "\n")
            i += 2
            continue
        if args[i] in ("--method", "-X"):
            method = args[i + 1]
            i += 2
            continue
        path = args[i]
        i += 1
    route = routes.get("routes", {}).get(f"{method} {path}")
    if route is None:
        if method != "GET":
            sys.stdout.write("{}")
            sys.exit(0)
        sys.stderr.write("gh: Not Found (HTTP 404)\n")
        sys.stdout.write(json.dumps({"message": "Not Found"}))
        sys.exit(1)
    body = route.get("body", "")
    sys.stdout.write(body if isinstance(body, str) else json.dumps(body))
    sys.stderr.write(route.get("stderr", ""))
    sys.exit(route.get("code", 0))

sys.stderr.write("fake gh: " + " ".join(args) + "\n")
sys.exit(2)
