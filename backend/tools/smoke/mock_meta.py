# Minimal stand-in for the Instagram Graph API used by the M0 smoke test. Not a contract test: it accepts what the client sends.
import json, sys
from http.server import BaseHTTPRequestHandler, HTTPServer
SENT = []
ACCOUNT = "17841400000000001"
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, obj, code=200):
        b = json.dumps(obj).encode()
        self.send_response(code); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            out = b""
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if size == 0:
                    self.rfile.readline(); break
                out += self.rfile.read(size); self.rfile.readline()
            return out.decode()
        n = int(self.headers.get("Content-Length") or 0); return self.rfile.read(n).decode()
    def do_GET(self):
        p = self.path.split("?")[0]
        if p == "/access_token": return self.send({"access_token": "IGAAlonglivedtokenvalue0123456789", "token_type": "bearer", "expires_in": 5184000})
        if p.endswith("/me"): return self.send({"user_id": ACCOUNT, "username": "shop_test", "name": "فروشگاه تست", "account_type": "BUSINESS"})
        if p == "/sent": return self.send(SENT)
        self.send({"error": {"message": "unknown", "code": 100}}, 404)
    def do_POST(self):
        p = self.path.split("?")[0]; b = self.body()
        if p == "/oauth/access_token": return self.send({"access_token": "IGshort", "user_id": int(ACCOUNT), "permissions": "instagram_business_basic,instagram_business_manage_messages"})
        if p.endswith("/me/subscribed_apps"): return self.send({"success": True})
        if p.endswith("/me/messages"):
            if self.headers.get("Authorization") != "Bearer IGAAlonglivedtokenvalue0123456789": return self.send({"error": {"code": 190, "message": "bad token"}}, 400)
            SENT.append(json.loads(b)); return self.send({"recipient_id": json.loads(b)["recipient"]["id"], "message_id": f"m_out_{len(SENT)}"})
        self.send({"error": {"message": "unknown", "code": 100}}, 404)
    def do_DELETE(self): self.send({"success": True})
HTTPServer(("127.0.0.1", 5999), H).serve_forever()
