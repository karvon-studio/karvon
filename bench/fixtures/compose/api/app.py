"""The notes API: GET /health, GET /notes and POST /notes. The notes are kept by the store service."""
import json
import os
import sys
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    store = ""

    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def call_store(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.store + path, data=data, method=method,
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=3) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            try:
                return error.code, json.load(error)
            except ValueError:
                return error.code, {"error": f"store answered {error.code}"}
        except (OSError, ValueError):
            return 503, {"error": "store unreachable"}

    def do_GET(self):
        if self.path == "/health":
            status, _ = self.call_store("GET", "/ping")
            ok = status == 200
            self.reply(200 if ok else 503, {"status": "ok" if ok else "store unreachable"})
        elif self.path == "/notes":
            self.reply(*self.call_store("GET", "/notes"))
        else:
            self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/notes":
            return self.reply(404, {"error": "not found"})
        length = int(self.headers.get("Content-Length") or 0)
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            return self.reply(400, {"error": "invalid JSON"})
        self.reply(*self.call_store("POST", "/notes", body))


def main():
    store = os.environ.get("STORE_URL", "").strip()
    if not store:
        sys.exit("config error: STORE_URL is not set")
    Handler.store = store.rstrip("/")
    port = int(os.environ.get("PORT", "8000"))
    print(f"api listening on :{port}, store at {Handler.store}")
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
