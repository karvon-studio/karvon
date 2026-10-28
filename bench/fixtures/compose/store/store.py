"""The notes store: keeps the notes in a JSON file. GET /ping, GET /notes and POST /notes."""
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DATA = os.path.join(os.environ.get("DATA_DIR", "/data"), "notes.json")
PORT = int(os.environ.get("PORT", "9000"))
lock = threading.Lock()


def load():
    try:
        with open(DATA, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return []


def save(notes):
    os.makedirs(os.path.dirname(DATA), exist_ok=True)
    with open(DATA + ".tmp", "w", encoding="utf-8") as f:
        json.dump(notes, f)
    os.replace(DATA + ".tmp", DATA)


class Handler(BaseHTTPRequestHandler):
    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/ping":
            self.reply(200, {"ok": True})
        elif self.path == "/notes":
            with lock:
                self.reply(200, load())
        else:
            self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/notes":
            return self.reply(404, {"error": "not found"})
        length = int(self.headers.get("Content-Length") or 0)
        try:
            text = str(json.loads(self.rfile.read(length) or b"{}")["text"])
        except (ValueError, KeyError, TypeError):
            return self.reply(400, {"error": 'expected {"text": "..."}'})
        with lock:
            notes = load()
            note = {"id": len(notes) + 1, "text": text}
            notes.append(note)
            save(notes)
        self.reply(201, note)


if __name__ == "__main__":
    print(f"store listening on :{PORT}, notes in {DATA}")
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
