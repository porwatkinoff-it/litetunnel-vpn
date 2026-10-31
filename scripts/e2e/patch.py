# The harness has no root to open a utun, so the app's TUN inbound is swapped
# for a local SOCKS/HTTP inbound. auto_detect_interface is TUN-only (it binds
# sockets to the default interface, which would keep them off loopback).
# Everything else (outbound, DNS, routing) is the app's config as built.
import json, sys
src, dst, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
c = json.load(open(src))
assert c["inbounds"][0]["type"] == "tun"
c["inbounds"] = [{"type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": port}]
c["route"].pop("auto_detect_interface", None)
c["log"]["level"] = "info"
json.dump(c, open(dst, "w"), indent=1)
