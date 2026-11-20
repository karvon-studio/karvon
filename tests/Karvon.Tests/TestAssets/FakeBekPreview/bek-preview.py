#!/usr/bin/env python3
"""A fake bek-preview for the Bek tests: bek-preview's API, on the port it is given, for a counter app (a tap on its button
adds one). The component's name says how it behaves: "slow" says it is ready after 3 seconds, "never" never says so, and
"flaky" answers its second state with what is not JSON. Its process id goes to <component>.pid."""
import json
import os
import struct
import sys
import threading
import time
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

component = sys.argv[1]
port = int(sys.argv[sys.argv.index("--port") + 1])
name = os.path.splitext(os.path.basename(component))[0]
with open(component + ".pid", "w") as pid:
    pid.write(str(os.getpid()))


def png(shade):
    """A one-pixel PNG of that grey."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"\x00" + bytes([shade] * 3))) + chunk(b"IEND", b""))


changes = threading.Condition()
state = {"version": 1, "screen": 1, "count": 0, "dark": False, "locale": "en-US", "width": 536, "height": 984, "asked": 0}
logs = [{"number": 1, "millis": 5, "source": "preview", "text": "started " + name}]


def snapshot():
    return {"version": state["version"], "screen": state["screen"],
            "app": {"name": name, "title": name.title(), "component": component}, "running": True, "error": None,
            "settings": {"width": state["width"], "height": state["height"], "dark": state["dark"], "locale": state["locale"],
                         "fixed_time": False},
            "timing": {"start": 12.0, "update": 0.1, "draw": 0.4, "updates": state["count"]}, "last_log": len(logs)}


def changed(screen, line=None):
    state["version"] += 1
    if screen:
        state["screen"] += 1
    if line:
        logs.append({"number": len(logs) + 1, "millis": 10 * (len(logs) + 1), "source": "app", "text": line})
    changes.notify_all()


class Api(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send(self, code, body, kind="application/json"):
        data = body if isinstance(body, bytes) else body.encode()
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        # http.server answers in HTTP/1.0 and closes the connection: said, so that the client does not send its next request on it.
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        query = parse_qs(url.query)
        with changes:
            if url.path == "/api/state":
                if "after" in query:
                    changes.wait_for(lambda: state["version"] > int(query["after"][0]), timeout=20)
                state["asked"] += 1
                if "flaky" in name and state["asked"] == 2:
                    return self.send(200, "not json")
                return self.send(200, json.dumps(snapshot()))
            if url.path == "/api/screen.png":
                return self.send(200, png(state["count"] * 40 % 256), "image/png")
            if url.path == "/api/tree.txt":
                return self.send(200, 'view @0,0 536x984\n  button.add {click} @218,220 100x48\n  text "%d" @260,120 16x20\n'
                                 % state["count"], "text/plain")
            if url.path == "/api/logs":
                after = int(query.get("after", ["0"])[0])
                return self.send(200, json.dumps([line for line in logs if line["number"] > after]))
            if url.path == "/api/store":
                return self.send(200, json.dumps([["count", str(state["count"])]]))
        self.send(404, "not found", "text/plain")

    def do_POST(self):
        url = urlparse(self.path)
        query = parse_qs(url.query)
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)).decode()
        with changes:
            if url.path == "/api/tap":
                x, y = float(query["x"][0]), float(query["y"][0])
                if 218 <= x <= 318 and 220 <= y <= 268:
                    state["count"] += 1
                    changed(True, "count %d" % state["count"])
                else:
                    changed(False)
            elif url.path == "/api/type":
                changed(True, "typed " + body)
            elif url.path == "/api/settings":
                if "dark" in query:
                    state["dark"] = query["dark"][0] == "true"
                if "locale" in query:
                    state["locale"] = query["locale"][0]
                if "size" in query:
                    state["width"], state["height"] = map(int, query["size"][0].split("x"))
                changed(True)
            elif url.path in ("/api/back", "/api/key", "/api/reload", "/api/reset", "/api/scroll"):
                changed(True)
            else:
                return self.send(400, "no such action: " + url.path, "text/plain")
            return self.send(200, json.dumps(snapshot()))


if "slow" in name:
    time.sleep(3)
server = ThreadingHTTPServer(("127.0.0.1", port), Api)
if "never" not in name:
    print("bek-preview: http://127.0.0.1:%d" % port, flush=True)
server.serve_forever()
