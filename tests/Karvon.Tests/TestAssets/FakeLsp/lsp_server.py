"""A language server for the tests, over stdio: it pushes a warning for each TODO in a file it is given (when it opens
and on each change), and answers completion and hover. What it does not serve it refuses, as a real server would.
Given a word (lsp_server.py FIXME), it is a linter's: it warns of that word instead."""
import json
import re
import sys

documents = {}
word = sys.argv[1] if len(sys.argv) > 1 else "TODO"


def read():
    length = 0
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            sys.exit(0)
        line = line.strip()
        if not line:
            break
        name, _, value = line.decode().partition(":")
        if name.lower() == "content-length":
            length = int(value)
    return json.loads(sys.stdin.buffer.read(length))


def send(message):
    data = json.dumps(message).encode()
    sys.stdout.buffer.write(b"Content-Length: %d\r\n\r\n" % len(data) + data)
    sys.stdout.buffer.flush()


def offset(text, position):
    lines = text.split("\n")
    return sum(len(l) + 1 for l in lines[:position["line"]]) + position["character"]


def publish(uri):
    text = documents[uri]
    diagnostics = []
    for i, line in enumerate(text.split("\n")):
        for match in re.finditer(re.escape(word), line):
            diagnostics.append({"range": {"start": {"line": i, "character": match.start()}, "end": {"line": i, "character": match.end()}},
                                "severity": 2, "source": "fake", "message": f"{word} left"})
    send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics", "params": {"uri": uri, "diagnostics": diagnostics}})


while True:
    message = read()
    method, params, id = message.get("method"), message.get("params") or {}, message.get("id")
    if method == "initialize":
        send({"jsonrpc": "2.0", "id": id, "result": {"capabilities": {"textDocumentSync": 2, "hoverProvider": True,
                                                                      "completionProvider": {"triggerCharacters": ["."]}},
                                                     "serverInfo": {"name": "fake"}}})
    elif method == "textDocument/didOpen":
        documents[params["textDocument"]["uri"]] = params["textDocument"]["text"]
        publish(params["textDocument"]["uri"])
    elif method == "textDocument/didChange":
        uri = params["textDocument"]["uri"]
        for change in params["contentChanges"]:
            if "range" in change:
                text = documents[uri]
                start, end = offset(text, change["range"]["start"]), offset(text, change["range"]["end"])
                documents[uri] = text[:start] + change["text"] + text[end:]
            else:
                documents[uri] = change["text"]
        publish(uri)
    elif method == "textDocument/completion":
        send({"jsonrpc": "2.0", "id": id, "result": {"isIncomplete": False, "items": [{"label": "fakeAlpha", "kind": 3}, {"label": "fakeBeta", "kind": 6}]}})
    elif method == "textDocument/hover":
        send({"jsonrpc": "2.0", "id": id, "result": {"contents": {"kind": "markdown", "value": "Fake hover"}}})
    elif method == "shutdown":
        send({"jsonrpc": "2.0", "id": id, "result": None})
    elif method == "exit":
        sys.exit(0)
    elif id is not None:
        send({"jsonrpc": "2.0", "id": id, "error": {"code": -32601, "message": f"{method} is not served"}})
