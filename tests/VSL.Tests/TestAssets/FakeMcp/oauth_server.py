#!/usr/bin/env python3
"""An MCP server over HTTP that wants an OAuth sign-in, for VSL's tests, on 127.0.0.1 (its port printed first).

It is its own authorization server: protected resource metadata, authorization server metadata, dynamic client
registration, /authorize (which signs in at once and redirects with a code), /token (PKCE checked; codes and refresh
tokens), and /mcp, which answers JSON-RPC only with a token it issued and says 401 with WWW-Authenticate otherwise.

  --client ID     a client given out beforehand: registration is refused (403), only ID signs in (redirect at localhost)
  --issuer-slash  the metadata's issuer ends with a slash its resource metadata does not have
"""
import argparse
import base64
import hashlib
import json
import secrets
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--client")
parser.add_argument("--issuer-slash", action="store_true")
options = parser.parse_args()

clients = {}
if options.client:
    clients[options.client] = None
codes = {}
tokens = set()
refresh_tokens = set()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    @property
    def base(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def send(self, status, body=None, headers=None, content_type="application/json"):
        data = b"" if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
        self.send_response(status)
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        if data:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
            return self.rfile.read(int(self.headers.get("Content-Length", "0") or 0))
        body = b""
        while True:
            size = int(self.rfile.readline().strip().split(b";")[0], 16)
            if size == 0:
                self.rfile.readline()
                return body
            body += self.rfile.read(size)
            self.rfile.readline()

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        print(f"got GET {url.path}", file=sys.stderr, flush=True)
        if url.path.startswith("/.well-known/oauth-protected-resource"):
            self.send(200, {"resource": self.base + "/mcp", "authorization_servers": [self.base], "scopes_supported": ["read"],
                            "bearer_methods_supported": ["header"]})
        elif url.path in ("/.well-known/oauth-authorization-server", "/.well-known/openid-configuration"):
            self.send(200, {
                "issuer": self.base + ("/" if options.issuer_slash else ""),
                "authorization_endpoint": self.base + "/authorize",
                "token_endpoint": self.base + "/token",
                "registration_endpoint": self.base + "/register",
                "response_types_supported": ["code"],
                "grant_types_supported": ["authorization_code", "refresh_token"],
                "code_challenge_methods_supported": ["S256"],
                "token_endpoint_auth_methods_supported": ["none"],
                "scopes_supported": ["read"],
                "authorization_response_iss_parameter_supported": True,
            })
        elif url.path == "/authorize":
            query = dict(urllib.parse.parse_qsl(url.query))
            client_id = query.get("client_id")
            redirect = query.get("redirect_uri", "")
            known = client_id in clients and (clients[client_id] is None and redirect.startswith("http://localhost:") or redirect in (clients[client_id] or []))
            if not known or query.get("code_challenge_method") != "S256":
                self.send(400, {"error": "invalid_request"})
                return
            code = secrets.token_urlsafe(16)
            codes[code] = (query["client_id"], query["redirect_uri"], query["code_challenge"])
            issuer = self.base + ("/" if options.issuer_slash else "")
            location = query["redirect_uri"] + "?" + urllib.parse.urlencode({"code": code, "state": query.get("state", ""), "iss": issuer})
            self.send(302, headers={"Location": location})
        else:
            self.send(405 if url.path == "/mcp" else 404)

    def do_POST(self):
        url = urllib.parse.urlparse(self.path)
        print(f"got POST {url.path}", file=sys.stderr, flush=True)
        body = self.read_body()
        if url.path == "/register" and options.client:
            self.send(403, {"error": "access_denied", "error_description": "Forbidden"})
        elif url.path == "/register":
            request = json.loads(body)
            client_id = "client-" + secrets.token_hex(4)
            clients[client_id] = request.get("redirect_uris", [])
            self.send(201, {"client_id": client_id, "redirect_uris": clients[client_id], "token_endpoint_auth_method": "none",
                            "grant_types": ["authorization_code", "refresh_token"], "response_types": ["code"]})
        elif url.path == "/token":
            form = dict(urllib.parse.parse_qsl(body.decode()))
            if form.get("grant_type") == "authorization_code":
                client_id, redirect_uri, challenge = codes.pop(form.get("code"), (None, None, None))
                verifier = form.get("code_verifier", "")
                expected = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")
                if client_id is None or expected != challenge or form.get("redirect_uri") != redirect_uri:
                    self.send(400, {"error": "invalid_grant"})
                    return
            elif form.get("grant_type") != "refresh_token" or form.get("refresh_token") not in refresh_tokens:
                self.send(400, {"error": "invalid_grant"})
                return
            token, refresh = "at-" + secrets.token_hex(8), "rt-" + secrets.token_hex(8)
            tokens.add(token)
            refresh_tokens.add(refresh)
            self.send(200, {"access_token": token, "token_type": "Bearer", "expires_in": 3600, "refresh_token": refresh, "scope": "read"})
        elif url.path == "/mcp":
            auth = self.headers.get("Authorization", "")
            if not auth.startswith("Bearer ") or auth[7:] not in tokens:
                self.send(401, {"jsonrpc": "2.0", "error": {"code": -32001, "message": "Sign in first"}, "id": None},
                          {"WWW-Authenticate": f'Bearer resource_metadata="{self.base}/.well-known/oauth-protected-resource/mcp", scope="read"'})
                return
            message = json.loads(body)
            if "id" not in message:
                self.send(202)
                return
            method = message.get("method")
            params = message.get("params") or {}
            if method == "initialize":
                result = {"protocolVersion": params.get("protocolVersion", "2025-06-18"), "capabilities": {"tools": {}},
                          "serverInfo": {"name": "signed", "version": "1.0"}}
            elif method == "tools/list":
                result = {"tools": [{"name": "whoami", "description": "Who VSL signed in as.", "inputSchema": {"type": "object", "properties": {}}}]}
            elif method == "tools/call":
                result = {"content": [{"type": "text", "text": "signed in"}]}
            else:
                self.send(200, {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": method}})
                return
            self.send(200, {"jsonrpc": "2.0", "id": message["id"], "result": result})
        else:
            self.send(404)


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
print(server.server_address[1], flush=True)
server.serve_forever()
