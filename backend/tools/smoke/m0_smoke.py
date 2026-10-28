# M0 end-to-end smoke test (BE-0.12) against local hosts and tools/smoke/mock_meta.py. See README "Smoke test".
import base64, hashlib, hmac, json, time, urllib.request, urllib.parse, urllib.error
API, INGRESS, META = "http://localhost:5080", "http://localhost:5090", "http://localhost:5999"
SECRET = b"test-app-secret"
def call(method, url, body=None, token=None, headers=None, raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token: req.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items(): req.add_header(k, v)
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a, **k): return None
    opener = urllib.request.build_opener(NoRedirect)
    try:
        r = opener.open(req); return r.status, r.read().decode(), dict(r.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(), dict(e.headers)
def otp_code(item):
    import re
    text = json.loads(item)["text"].translate(str.maketrans("۰۱۲۳۴۵۶۷۸۹", "0123456789"))
    return re.search(r"\d{6}", text).group(0)
def ok(cond, msg):
    print(("PASS " if cond else "FAIL ") + msg)
    if not cond: raise SystemExit(1)

import subprocess
subprocess.run(["docker", "exec", "pishkhan-postgres-1", "psql", "-U", "pishkhan", "-qc",
    "TRUNCATE channels.channel_accounts, conversations.messages, conversations.conversations, conversations.contacts_channel_identities, conversations.contacts, conversations.external_message_keys"], check=True)
# 1. OTP login with ALTCHA proof of work
s, b, _ = call("GET", API + "/api/v1/public/altcha/challenge"); ch = json.loads(b)
n = next(i for i in range(ch["maxNumber"] + 1) if hashlib.sha256((ch["salt"] + str(i)).encode()).hexdigest() == ch["challenge"])
payload = base64.b64encode(json.dumps({"algorithm": "SHA-256", "challenge": ch["challenge"], "number": n, "salt": ch["salt"], "signature": ch["signature"]}).encode()).decode()
phone = "0912" + str(int(time.time()))[-7:]
s, b, _ = call("POST", API + "/api/v1/auth/otp/request", {"phone": phone, "altcha": payload}); ok(s in (200, 202), f"OTP requested ({s})")
s, b, _ = call("GET", API + "/dev/sms"); sms = json.loads(b)[0] if s == 200 else ""
code = otp_code(sms)
s, b, _ = call("POST", API + "/api/v1/auth/otp/verify", {"phone": phone, "code": code}); ok(s == 200, f"OTP verified ({s} {b[:120]})")
token = json.loads(b)["accessToken"]

# 2. Workspace
s, b, _ = call("POST", API + "/api/v1/workspaces", {"name": "فروشگاه پیشخوان", "industry": "clothing"}, token); ok(s in (200, 201), f"workspace created ({s} {b[:200]})")
ws = json.loads(b)["id"]

# 3. Connect Instagram through the (mock) OAuth flow
s, b, _ = call("POST", f"{API}/api/v1/workspaces/{ws}/channels/instagram/connect", None, token); ok(s == 200, f"connect URL ({s} {b[:200]})")
state = urllib.parse.parse_qs(urllib.parse.urlparse(json.loads(b)["authorizeUrl"]).query)["state"][0]
s, b, h = call("GET", f"{API}/api/v1/channels/instagram/callback?code=abc&state={urllib.parse.quote(state)}")
ok(s == 302 and "status=connected" in h.get("Location", ""), f"OAuth callback redirects connected ({s} {h.get('Location')})")
s, b, h = call("GET", f"{API}/api/v1/channels/instagram/callback?code=abc&state={urllib.parse.quote(state)}")
ok("invalid_state" in h.get("Location", ""), "OAuth state is single use")
s, b, _ = call("GET", f"{API}/api/v1/workspaces/{ws}/channels", None, token); chans = json.loads(b)
ok(len(chans) == 1 and chans[0]["status"] == "active", f"channel active: {chans}")

# 4. Webhook verify and signature
s, b, _ = call("GET", INGRESS + "/webhooks/instagram?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=12345"); ok(s == 200 and b == "12345", "webhook verify challenge echoed")
s, b, _ = call("GET", INGRESS + "/webhooks/instagram?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1"); ok(s == 403, "wrong verify token rejected")
mid = "m_in_" + str(int(time.time() * 1000))
event = {"object": "instagram", "entry": [{"id": "17841400000000001", "time": int(time.time()), "messaging": [{
    "sender": {"id": "990011"}, "recipient": {"id": "17841400000000001"}, "timestamp": int(time.time() * 1000),
    "message": {"mid": mid, "text": "سلام، قيمت اين مانتو چنده؟"}}]}]}
raw = json.dumps(event, ensure_ascii=False).encode()
s, b, _ = call("POST", INGRESS + "/webhooks/instagram", raw=raw, headers={"X-Hub-Signature-256": "sha256=" + "0" * 64}); ok(s == 401, f"bad signature returns 401 ({s})")
sig = "sha256=" + hmac.new(SECRET, raw, hashlib.sha256).hexdigest()
s, b, _ = call("POST", INGRESS + "/webhooks/instagram", raw=raw, headers={"X-Hub-Signature-256": sig}); ok(s == 200, f"signed webhook accepted ({s} {b})")
s, b, _ = call("POST", INGRESS + "/webhooks/instagram", raw=raw, headers={"X-Hub-Signature-256": sig}); ok(s == 200, "duplicate webhook acknowledged")

# 5. Stored and echoed
sent = []
for _ in range(60):
    sent = json.loads(urllib.request.urlopen(META + "/sent").read())
    if any(m["recipient"]["id"] == "990011" and "مانتو" in m["message"]["text"] for m in sent): break
    time.sleep(1)
ok(any(m["recipient"]["id"] == "990011" for m in sent), f"echo reply sent to Instagram: {sent[-1:] }")
s, b, _ = call("GET", f"{API}/api/v1/workspaces/{ws}/conversations", None, token); convs = json.loads(b)["items"]
ok(len(convs) == 1 and convs[0]["unreadCount"] == 1, f"one conversation, one unread: {convs}")
time.sleep(2)
s, b, _ = call("GET", f"{API}/api/v1/workspaces/{ws}/conversations/{convs[0]['id']}/messages", None, token); msgs = json.loads(b)["items"]
print(json.dumps(msgs, ensure_ascii=False, indent=1))
ok([m["direction"] for m in msgs] == ["outbound", "inbound"] and msgs[0]["status"] == "sent", "inbound stored, outbound sent")
ok(msgs[1]["text"] == "سلام، قيمت اين مانتو چنده؟", "raw text kept")

# 6. Tenant isolation: another user cannot see this workspace
s, b, _ = call("GET", API + "/api/v1/public/altcha/challenge"); ch = json.loads(b)
n = next(i for i in range(ch["maxNumber"] + 1) if hashlib.sha256((ch["salt"] + str(i)).encode()).hexdigest() == ch["challenge"])
payload = base64.b64encode(json.dumps({"algorithm": "SHA-256", "challenge": ch["challenge"], "number": n, "salt": ch["salt"], "signature": ch["signature"]}).encode()).decode()
phone2 = "0935" + str(int(time.time()))[-7:]
call("POST", API + "/api/v1/auth/otp/request", {"phone": phone2, "altcha": payload})
s, b, _ = call("GET", API + "/dev/sms"); code2 = otp_code(json.loads(b)[0])
s, b, _ = call("POST", API + "/api/v1/auth/otp/verify", {"phone": phone2, "code": code2}); token2 = json.loads(b)["accessToken"]
s, b, _ = call("GET", f"{API}/api/v1/workspaces/{ws}/conversations", None, token2); ok(s == 404, f"other user's request is 404 ({s})")
print("ALL PASSED")
