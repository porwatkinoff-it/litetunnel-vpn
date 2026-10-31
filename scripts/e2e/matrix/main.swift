import Foundation

// Writes one config per (link, settings variant) for `sing-box check`.
// usage: gen <out-dir> <ruleset-dir>
let out = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
let ruleSets = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

let pbk = "9dE5PdEbZn5eXp9ygFXDFLA3e3sY003ritoKVO_G6nM"
let vmessJSON = #"{"v":"2","ps":"vmess-ws","add":"vm.example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","scy":"auto","net":"ws","type":"none","host":"vm.example.com","path":"/vm","tls":"tls","sni":"vm.example.com","fp":"firefox"}"#
let links: [(String, String)] = [
    ("vless-reality", "vless://11111111-2222-3333-4444-555555555555@203.0.113.10:443?encryption=none&security=reality&sni=www.example.com&fp=chrome&pbk=\(pbk)&sid=6ba85179e30d4fc2&type=tcp&flow=xtls-rprx-vision#R"),
    ("vless-reality-grpc", "vless://11111111-2222-3333-4444-555555555555@r.example.com:443?security=reality&sni=www.example.com&fp=safari&pbk=\(pbk)&sid=ab&type=grpc&serviceName=svc#RG"),
    ("vless-ws-tls", "vless://11111111-2222-3333-4444-555555555555@cdn.example.com:443?security=tls&sni=cdn.example.com&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com&alpn=h2%2Chttp%2F1.1#W"),
    ("vless-httpupgrade", "vless://11111111-2222-3333-4444-555555555555@203.0.113.11:80?type=httpupgrade&path=%2Fup&host=h.example.com#U"),
    ("vless-h2", "vless://11111111-2222-3333-4444-555555555555@203.0.113.12:443?security=tls&type=http&path=%2Fh2&host=h2.example.com#H2"),
    ("vmess-ws-tls", "vmess://" + Data(vmessJSON.utf8).base64EncodedString()),
    ("trojan-grpc", "trojan://pass@tr.example.com:443?security=tls&sni=tr.example.com&type=grpc&serviceName=tr&allowInsecure=1#T"),
    ("trojan-tcp", "trojan://pass@203.0.113.13:443#T2"),
    ("ss-2022", "ss://" + Data("2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA==".utf8).base64EncodedString() + "@203.0.113.14:8388#S"),
    ("ss-aead", "ss://" + Data("chacha20-ietf-poly1305:pw".utf8).base64EncodedString() + "@ss.example.com:8388#S2"),
    ("hy2-obfs", "hy2://secret@203.0.113.20:443?sni=example.com&obfs=salamander&obfs-password=xyz#HY"),
    ("hy2-hop", "hysteria2://p%40ss@hy.example.com:443?mport=20000-30000&mportHopInt=30&insecure=1&pinSHA256=AA#HOP"),
    ("tuic", "tuic://11111111-2222-3333-4444-555555555555:pw@tu.example.com:443?congestion_control=bbr&alpn=h3&sni=tu.example.com&udp_relay_mode=native#TU"),
    ("socks", "socks://dXNlcjpwYXNz@203.0.113.15:1080#SO"),
]

func variant(_ f: (inout AppSettings) -> Void) -> AppSettings { var s = AppSettings(); f(&s); return s }
let variants: [(String, AppSettings, URL?)] = [
    ("default", AppSettings(), nil),
    ("ru-rulesets-ads", variant { $0.blockAds = true }, ruleSets),
    ("global-custom", variant {
        $0.routing = .global
        $0.directDomains = ["bank.example"]; $0.proxyDomains = ["youtube.com"]; $0.blockDomains = ["ads.example"]
        $0.remoteDNS = DNSServer(kind: .tls, address: "dns.google"); $0.directDNS = DNSServer(kind: .https, address: "common.dot.dns.yandex.net")
        $0.ipv6 = true; $0.mtu = 1400; $0.tunStack = .gvisor; $0.tlsFragment = true; $0.killSwitch = true
    }, ruleSets),
    ("minimal", variant {
        $0.sniffing = false; $0.tunStack = .system; $0.logLevel = .debug; $0.remoteDNS = DNSServer(kind: .udp, address: "8.8.8.8")
    }, nil),
]

var parseFailures = 0
for (name, link) in links {
    let parsed: ParsedServer
    do { parsed = try ShareLink.parse(link) } catch { print("PARSE FAIL \(name): \(error)"); parseFailures += 1; continue }
    let server = Server(subscriptionID: nil, parsed: parsed)
    for (vname, settings, dir) in variants {
        let json = ConfigBuilder.build(server: server, settings: settings, ruleSetDirectory: dir)
        try json.encoded(pretty: true).write(to: out.appendingPathComponent("\(name)__\(vname).json"))
    }
}
print("\(links.count - parseFailures) links x \(variants.count) variants written")
if parseFailures > 0 { exit(1) }
