"""A debug adapter for the tests, over stdio: its launch asks the client to run the program (runInTerminal, as
kotlin-lsp's Gradle launch does), and it ends (terminated) when the program has started, before the program is done."""
import json
import sys

seq = 0


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
    global seq
    seq += 1
    message["seq"] = seq
    data = json.dumps(message).encode()
    sys.stdout.buffer.write(b"Content-Length: %d\r\n\r\n" % len(data) + data)
    sys.stdout.buffer.flush()


def respond(request, body=None):
    send({"type": "response", "request_seq": request["seq"], "command": request["command"], "success": True, "body": body or {}})


launch = None
while True:
    message = read()
    if message["type"] == "response" and message["command"] == "runInTerminal":
        # The program runs where the client runs it: this adapter is done with it.
        respond(launch)
        send({"type": "event", "event": "output", "body": {"category": "console", "output": f"started {message['body']['processId']}\n"}})
        send({"type": "event", "event": "terminated", "body": {}})
        continue
    if message["type"] != "request":
        continue
    command = message["command"]
    if command == "initialize":
        if not message["arguments"].get("supportsRunInTerminalRequest"):
            sys.exit(1)
        respond(message, {"supportsConfigurationDoneRequest": True})
    elif command == "launch":
        launch = message
        send({"type": "event", "event": "initialized", "body": {}})
    elif command == "configurationDone":
        respond(message)
        arguments = launch["arguments"]
        send({"type": "request", "command": "runInTerminal", "arguments": {
            "kind": "integrated", "title": "program", "cwd": arguments["cwd"], "args": arguments["program"],
            "env": {"KARVON_FAKE_DAP": "set", "KARVON_FAKE_UNSET": None}}})
    elif command == "disconnect":
        respond(message)
        sys.exit(0)
    else:
        respond(message)
