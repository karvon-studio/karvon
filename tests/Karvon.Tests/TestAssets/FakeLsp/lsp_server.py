"""A language server for the tests, over stdio: it pushes a warning for each TODO in a file it is given (when it opens
and on each change), and answers completion and hover. What it does not serve it refuses, as a real server would.
Given a word (lsp_server.py FIXME), it is a linter's: it warns of that word instead. With --hints it also gives inlay hints
(value: after each "call("), a code lens on each "def " line (resolved: "2 references"), and a Fix All of the TODOs, as
Roslyn offers one (its client command, then codeAction/resolveFixAll). With --symbols it outlines a file (a function for
each "def name" line), only an open one, as TypeScript's server does."""
import json
import re
import sys

documents = {}
hints = "--hints" in sys.argv
symbols = "--symbols" in sys.argv
word = next((a for a in sys.argv[1:] if not a.startswith("--")), "TODO")


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
        capabilities = {"textDocumentSync": 2, "hoverProvider": True, "completionProvider": {"triggerCharacters": ["."]}}
        if hints:
            capabilities.update({"inlayHintProvider": True, "codeLensProvider": {"resolveProvider": True}, "codeActionProvider": {"resolveProvider": True}})
        send({"jsonrpc": "2.0", "id": id, "result": {"capabilities": capabilities, "serverInfo": {"name": "fake"}}})
    elif method == "textDocument/didOpen":
        documents[params["textDocument"]["uri"]] = params["textDocument"]["text"]
        publish(params["textDocument"]["uri"])
    elif method == "textDocument/didClose":
        documents.pop(params["textDocument"]["uri"], None)
    elif method == "textDocument/documentSymbol" and symbols:
        text = documents.get(params["textDocument"]["uri"])
        if text is None:
            send({"jsonrpc": "2.0", "id": id, "error": {"code": -32602, "message": "No project: the file is not open"}})
            continue
        found = []
        for i, line in enumerate(text.split("\n")):
            if line.startswith("def "):
                name = line[4:].strip()
                found.append({"name": name, "kind": 12, "children": [],
                              "range": {"start": {"line": i, "character": 0}, "end": {"line": i, "character": len(line)}},
                              "selectionRange": {"start": {"line": i, "character": 4}, "end": {"line": i, "character": 4 + len(name)}}})
        send({"jsonrpc": "2.0", "id": id, "result": found})
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
    elif method == "textDocument/inlayHint" and hints:
        text = documents[params["textDocument"]["uri"]]
        found = [{"position": {"line": i, "character": m.end()}, "label": "value:", "kind": 2, "paddingRight": True}
                 for i, line in enumerate(text.split("\n")) for m in re.finditer(re.escape("call("), line)]
        send({"jsonrpc": "2.0", "id": id, "result": found})
    elif method == "textDocument/codeLens" and hints:
        text = documents[params["textDocument"]["uri"]]
        # A member's lens at its name, as indented as it is.
        lenses = [{"range": {"start": {"line": i, "character": len(line) - len(line.lstrip()) + 4}, "end": {"line": i, "character": len(line)}}, "data": {"line": i}}
                  for i, line in enumerate(text.split("\n")) if line.lstrip().startswith("def ")]
        send({"jsonrpc": "2.0", "id": id, "result": lenses})
    elif method == "codeLens/resolve" and hints:
        params["command"] = {"title": "2 references", "command": "fake.references"}
        send({"jsonrpc": "2.0", "id": id, "result": params})
    elif method == "textDocument/codeAction" and hints:
        send({"jsonrpc": "2.0", "id": id, "result": [{"title": "Fix all TODOs", "kind": "quickfix", "command": {
            "title": "Fix all", "command": "roslyn.client.fixAllCodeAction",
            "arguments": [{"UniqueIdentifier": "fix-todo", "FixAllFlavors": ["Document", "Project", "Solution"]}]}}]})
    elif method == "codeAction/resolveFixAll" and hints:
        uri, text = next(iter(documents.items()))
        edits = [{"range": {"start": {"line": i, "character": m.start()}, "end": {"line": i, "character": m.end()}}, "newText": "DONE"}
                 for i, line in enumerate(text.split("\n")) for m in re.finditer("TODO", line)]
        send({"jsonrpc": "2.0", "id": id, "result": {"title": params["title"] + " in " + params["scope"], "edit": {"changes": {uri: edits}}}})
    elif method == "shutdown":
        send({"jsonrpc": "2.0", "id": id, "result": None})
    elif method == "exit":
        sys.exit(0)
    elif id is not None:
        send({"jsonrpc": "2.0", "id": id, "error": {"code": -32601, "message": f"{method} is not served"}})
