#!/usr/bin/env python3
"""A small MCP server over stdio, for Karvon's tests: newline-delimited JSON-RPC, one tool, one resource, one prompt.

Every method it gets is written to stderr ("got tools/call"), which Karvon shows in the server's log.
"""
import json
import sys

TOOLS = [{
    "name": "echo",
    "description": "Says the text back, as many times as asked.",
    "inputSchema": {
        "type": "object",
        "properties": {
            "text": {"type": "string", "description": "What to say."},
            "times": {"type": "integer", "description": "How many times.", "default": 1},
            "shout": {"type": "boolean"},
            "mood": {"enum": ["calm", "glad"]},
            "tags": {"type": "array", "items": {"type": "string"}},
        },
        "required": ["text"],
    },
    "annotations": {"readOnlyHint": True},
}]

RESOURCES = [{"uri": "echo://readme", "name": "readme", "description": "About this server.", "mimeType": "text/markdown"}]

PROMPTS = [{"name": "greet", "description": "A greeting.", "arguments": [{"name": "who", "required": True}]}]


def answer(id, result=None, error=None):
    message = {"jsonrpc": "2.0", "id": id}
    if error is not None:
        message["error"] = error
    else:
        message["result"] = result
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def handle(message):
    method = message.get("method")
    params = message.get("params") or {}
    print(f"got {method}", file=sys.stderr, flush=True)
    if "id" not in message:
        return
    id = message["id"]
    if method == "initialize":
        answer(id, {
            "protocolVersion": params.get("protocolVersion", "2025-06-18"),
            "capabilities": {"tools": {}, "resources": {}, "prompts": {}},
            "serverInfo": {"name": "echo", "version": "1.0"},
        })
    elif method == "ping":
        answer(id, {})
    elif method == "tools/list":
        answer(id, {"tools": TOOLS})
    elif method == "tools/call":
        args = params.get("arguments") or {}
        if params.get("name") != "echo":
            answer(id, {"content": [{"type": "text", "text": "No such tool."}], "isError": True})
            return
        text = str(args.get("text", "")) * int(args.get("times", 1))
        if args.get("shout"):
            text = text.upper()
        if args.get("tags"):
            text += " " + ",".join(args["tags"])
        answer(id, {"content": [{"type": "text", "text": text}]})
    elif method == "resources/list":
        answer(id, {"resources": RESOURCES})
    elif method == "resources/read":
        answer(id, {"contents": [{"uri": params.get("uri"), "mimeType": "text/markdown", "text": "# Echo\nIt says things back."}]})
    elif method == "prompts/list":
        answer(id, {"prompts": PROMPTS})
    elif method == "prompts/get":
        who = (params.get("arguments") or {}).get("who", "you")
        answer(id, {"messages": [{"role": "user", "content": {"type": "text", "text": f"Hello, {who}!"}}]})
    else:
        answer(id, error={"code": -32601, "message": f"Method not found: {method}"})


for line in sys.stdin:
    line = line.strip()
    if line:
        handle(json.loads(line))
