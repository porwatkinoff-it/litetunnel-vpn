# Serves the test subscription the way providers do: base64 body plus
# subscription-userinfo, profile-title and profile-update-interval headers.
# /xray serves the same kind of servers as Xray JSON, as Remnawave gives it to
# Happ: an array of full Xray configs, one per server.
# Like a Remnawave panel with a device limit, it gives a client that sends no
# valid x-hwid a placeholder server and x-hwid-not-supported: true.
import base64, http.server, json, os, re, sys, time
e = sys.argv[1]
def key(name):
    for line in open(os.path.join(e, "keys", name)):
        if line.startswith("PublicKey:"): return line.split(":", 1)[1].strip()
good, wrong = key("reality.txt"), key("reality-wrong.txt")
uuid = "11111111-2222-3333-4444-555555555555"
reality = f"vless://{uuid}@127.0.0.1:20443?encryption=none&security=reality&sni=www.example.com&fp=chrome&pbk={{pbk}}&sid=6ba85179e30d4fc2&type=tcp&flow=xtls-rprx-vision#{{name}}"
lines = [
    reality.format(pbk=good, name="E2E%20Reality"),
    "hy2://secret@127.0.0.1:20444?sni=example.com&insecure=1&obfs=salamander&obfs-password=xyz#E2E%20HY2",
    reality.format(pbk=wrong, name="BAD%20Reality%20key"),
    "hy2://secret@127.0.0.1:20444?sni=example.com&insecure=1&obfs=salamander&obfs-password=nope#BAD%20HY2%20obfs",
    f"vless://{uuid}@127.0.0.1:1?type=xhttp&security=tls#Skipped%20XHTTP",
]
body = base64.b64encode("\n".join(lines).encode())

def xray(remarks, proxy):
    return {"remarks": remarks,
            "log": {"loglevel": "warning"},
            "inbounds": [{"tag": "socks", "protocol": "socks", "listen": "127.0.0.1", "port": 10808}],
            "outbounds": [proxy, {"tag": "direct", "protocol": "freedom"}, {"tag": "block", "protocol": "blackhole"}],
            "routing": {"domainStrategy": "IPIfNonMatch", "rules": [{"type": "field", "outboundTag": "block", "domain": ["geosite:category-ads-all"]}]}}
def xray_reality(pbk):
    return {"tag": "proxy", "protocol": "vless",
            "settings": {"vnext": [{"address": "127.0.0.1", "port": 20443,
                                    "users": [{"id": uuid, "encryption": "none", "flow": "xtls-rprx-vision"}]}]},
            "streamSettings": {"network": "tcp", "security": "reality",
                               "realitySettings": {"serverName": "www.example.com", "fingerprint": "chrome",
                                                   "publicKey": pbk, "shortId": "6ba85179e30d4fc2", "spiderX": ""}}}
def xray_hy2(auth):
    return {"tag": "proxy", "protocol": "hysteria", "settings": {"version": 2, "address": "127.0.0.1", "port": 20445},
            "streamSettings": {"network": "hysteria", "security": "tls",
                               "tlsSettings": {"serverName": "example.com", "allowInsecure": True, "alpn": ["h3"]},
                               "hysteriaSettings": {"version": 2, "auth": auth}}}
xray_body = json.dumps([
    xray("E2E Xray Reality", xray_reality(good)),
    xray("E2E Xray HY2", xray_hy2("secret")),
    xray("BAD Xray Reality key", xray_reality(wrong)),
    xray("BAD Xray HY2 auth", xray_hy2("nope")),
    xray("Skipped Xray XHTTP", {"tag": "proxy", "protocol": "vless",
                                "settings": {"vnext": [{"address": "127.0.0.1", "port": 1, "users": [{"id": uuid}]}]},
                                "streamSettings": {"network": "xhttp", "security": "tls"}}),
], ensure_ascii=False, indent=1).encode()

placeholder = base64.b64encode(b"vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1?type=tcp#App%20not%20supported")
expire = int(time.time()) + 30 * 86400
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        bodies = {"/sub": (body, "text/plain"), "/xray": (xray_body, "application/json")}
        if self.path not in bodies:
            self.send_response(404); self.end_headers(); return
        hwid = self.headers.get("x-hwid") or ""
        ok = re.fullmatch(r"[A-Za-z0-9=-]{10,64}", hwid) is not None
        out, kind = bodies[self.path] if ok else (placeholder, "text/plain")
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("x-hwid-active", "true")
        self.send_header("x-hwid-not-supported", "false" if ok else "true")
        self.send_header("subscription-userinfo", f"upload=1048576; download=2097152; total=10737418240; expire={expire}")
        self.send_header("profile-title", "base64:" + base64.b64encode("Тест e2e".encode()).decode())
        self.send_header("profile-update-interval", "6")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)
        device = " ".join(f"{h}={self.headers.get(h)}" for h in ("x-device-os", "x-ver-os", "x-device-model"))
        sys.stderr.write(f"{self.path} fetched, UA={self.headers.get('User-Agent')} hwid={hwid or '-'} {device}" +
                         ("" if ok else " -> placeholder") + "\n")
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", 18090), H).serve_forever()
