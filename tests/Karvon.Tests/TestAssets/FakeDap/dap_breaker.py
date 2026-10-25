"""A debug adapter for the debugger windows' tests, over stdio: it says it takes conditions, hit counts, log messages and
two exception filters; it stops at the first breakpoint it is given, has a local (total) to read and assign, and logs
every request it gets (as JSON lines, to the file FAKE_DAP_LOG names)."""
import json
import os
import sys

seq = 0
LOG = os.environ.get("FAKE_DAP_LOG")


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


def respond(request, body=None, error=None):
    response = {"type": "response", "request_seq": request["seq"], "command": request["command"], "success": error is None, "body": body or {}}
    if error is not None:
        response["message"] = error
    send(response)


launch = None
breakpoints = {}
total = 42
ids = 0
while True:
    message = read()
    if message["type"] != "request":
        continue
    command = message["command"]
    arguments = message.get("arguments") or {}
    if LOG:
        with open(LOG, "a") as log:
            log.write(json.dumps({"command": command, "arguments": arguments}, sort_keys=True) + "\n")
    if command == "initialize":
        respond(message, {
            "supportsConfigurationDoneRequest": True, "supportsConditionalBreakpoints": True, "supportsHitConditionalBreakpoints": True,
            "supportsLogPoints": True,
            "exceptionBreakpointFilters": [
                {"filter": "all", "label": "All Exceptions", "default": False},
                {"filter": "user-unhandled", "label": "User-Unhandled Exceptions", "default": True, "description": "Thrown and not caught in your code"},
            ],
        })
    elif command == "launch":
        launch = message
        send({"type": "event", "event": "initialized", "body": {}})
    elif command == "setBreakpoints":
        path = arguments["source"]["path"]
        breakpoints[path] = arguments.get("breakpoints", [])
        result = []
        for b in breakpoints[path]:
            ids += 1
            result.append({"id": ids, "verified": True, "line": b["line"]})
        respond(message, {"breakpoints": result})
    elif command == "configurationDone":
        respond(message)
        respond(launch)
        send({"type": "event", "event": "stopped", "body": {"reason": "breakpoint", "threadId": 1, "allThreadsStopped": True}})
    elif command == "threads":
        respond(message, {"threads": [{"id": 1, "name": "Main Thread"}]})
    elif command == "stackTrace":
        path, lines = next(((p, b) for p, b in breakpoints.items() if b), (launch["arguments"]["program"], [{"line": 1}]))
        respond(message, {"stackFrames": [{"id": 1, "name": "Program.Main", "line": lines[0]["line"], "column": 1, "source": {"path": path}}], "totalFrames": 1})
    elif command == "scopes":
        respond(message, {"scopes": [{"name": "Locals", "variablesReference": 1, "expensive": False}]})
    elif command == "variables":
        respond(message, {"variables": [{"name": "total", "value": str(total), "type": "int", "variablesReference": 0}]})
    elif command == "evaluate":
        expression = arguments["expression"].replace(" ", "")
        if expression == "total":
            respond(message, {"result": str(total), "type": "int", "variablesReference": 0})
        elif expression.startswith("total="):
            total = int(expression[6:])
            respond(message, {"result": str(total), "type": "int", "variablesReference": 0})
        else:
            respond(message, error=f"error CS0103: The name '{arguments['expression']}' does not exist in the current context")
    elif command == "continue":
        respond(message, {"allThreadsContinued": True})
        send({"type": "event", "event": "terminated", "body": {}})
    elif command == "disconnect":
        respond(message)
        sys.exit(0)
    else:
        respond(message)
