# Writes server.json: VLESS-Reality and Hysteria2 inbounds (with and without
# obfs) plus a local TLS decoy that Reality borrows its handshake from. Only
# the server can resolve e2e.test, so a page fetched by that name must have
# come through the tunnel.
import json, os, sys
e = sys.argv[1]
keys = dict(line.split(": ", 1) for line in open(os.path.join(e, "keys/reality.txt")).read().split("\n") if ": " in line)
cert, key = os.path.join(e, "keys/cert.pem"), os.path.join(e, "keys/key.pem")
uuid = "11111111-2222-3333-4444-555555555555"
config = {
    "log": {"level": "info", "timestamp": True},
    "dns": {"servers": [{"type": "hosts", "tag": "hosts", "predefined": {"e2e.test": ["127.0.0.1"]}}]},
    "inbounds": [
        {"type": "vless", "tag": "vless-in", "listen": "127.0.0.1", "listen_port": 20443,
         "users": [{"uuid": uuid, "flow": "xtls-rprx-vision"}],
         "tls": {"enabled": True, "server_name": "www.example.com",
                 "reality": {"enabled": True, "handshake": {"server": "127.0.0.1", "server_port": 18444},
                             "private_key": keys["PrivateKey"].strip(), "short_id": ["6ba85179e30d4fc2"]}}},
        {"type": "hysteria2", "tag": "hy2-in", "listen": "127.0.0.1", "listen_port": 20444,
         "obfs": {"type": "salamander", "password": "xyz"},
         "users": [{"password": "secret"}],
         "tls": {"enabled": True, "alpn": ["h3"], "certificate_path": cert, "key_path": key}},
        # The same without obfs: Xray JSON cannot carry salamander yet.
        {"type": "hysteria2", "tag": "hy2-plain", "listen": "127.0.0.1", "listen_port": 20445,
         "users": [{"password": "secret"}],
         "tls": {"enabled": True, "alpn": ["h3"], "certificate_path": cert, "key_path": key}},
        {"type": "http", "tag": "decoy", "listen": "127.0.0.1", "listen_port": 18444,
         "tls": {"enabled": True, "certificate_path": cert, "key_path": key}},
    ],
    "outbounds": [{"type": "direct", "tag": "direct"}],
    "route": {"default_domain_resolver": "hosts", "final": "direct"},
}
json.dump(config, open(os.path.join(e, "server.json"), "w"), indent=1)
