import Foundation

/// Several servers behind one entry: what an Xray config with a balancer
/// ("Авто-выбор") is. It runs as a sing-box urltest group, which picks the
/// fastest member and moves off one that stops answering.
///
/// Stored in `outbound` as `{"type": "urltest", "outbounds": [<member>...]}`
/// with the member outbounds inline; ConfigBuilder gives them tags.
public enum ServerGroup {
    static let type = "urltest"
    /// sing-box tests every 3 minutes by default, one request per member.
    /// Rarer tests cost fewer wake-ups; a dead member is still left on the
    /// next test or the next failed connection.
    static let interval = "10m"

    static func make(_ members: [ParsedServer], name: String) -> ParsedServer {
        let first = members[0]
        let outbound: JSONValue = ["type": .string(type), "outbounds": .array(members.map(\.outbound))]
        return ParsedServer(name: name, proto: first.proto, host: first.host, port: first.port, outbound: outbound, shareLink: nil)
    }

    /// The member outbounds, or nil when the outbound is a single server.
    static func members(of outbound: JSONValue) -> [JSONValue]? {
        guard outbound["type"]?.stringValue == type else { return nil }
        return outbound["outbounds"]?.arrayValue
    }

    /// The outbounds to put in the config, the first of them tagged `tag`.
    static func outbounds(_ outbound: JSONValue, tag: String) -> [JSONValue] {
        guard let members = members(of: outbound) else {
            var single = outbound
            single["tag"] = .string(tag)
            return [single]
        }
        let tagged = members.enumerated().map { index, member -> JSONValue in
            var member = member
            member["tag"] = .string("\(tag)-\(index + 1)")
            return member
        }
        let group: JSONValue = [
            "type": .string(type),
            "tag": .string(tag),
            "outbounds": .strings(tagged.compactMap { $0["tag"]?.stringValue }),
            "interval": .string(interval),
        ]
        return [group] + tagged
    }
}

extension Server {
    /// The servers of a group, each as its own Server; empty for a single server.
    public var members: [Server] {
        (ServerGroup.members(of: outbound) ?? []).compactMap { member in
            guard let type = member["type"]?.stringValue, let proto = ProxyProtocol(rawValue: type),
                  let host = member["server"]?.stringValue, let port = member["server_port"]?.intValue
            else { return nil }
            let parsed = ParsedServer(name: "\(host):\(port)", proto: proto, host: host, port: port, outbound: member, shareLink: nil)
            return Server(subscriptionID: subscriptionID, parsed: parsed)
        }
    }
}
