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
    /// The row the app adds to a subscription itself. Marked in the outbound,
    /// which keeps the saved state readable by older builds; the mark and the
    /// member names never reach the core, see `outbounds(_:tag:)`.
    static let autoName = "Авто-выбор"
    static let autoKey = "auto"

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

    /// What says two outbounds are the same server: all of it but the name.
    static func key(_ outbound: JSONValue) -> JSONValue {
        var key = outbound
        key["tag"] = nil
        return key
    }

    /// Every server of a list once: the single rows first, named after their
    /// row, then group members those do not cover. Auto rows add nothing.
    static func uniqueMembers(of list: [Server]) -> [JSONValue] {
        var seen = Set<JSONValue>()
        var result: [JSONValue] = []
        for server in list where !server.isGroup && seen.insert(key(server.outbound)).inserted {
            var member = server.outbound
            member["tag"] = .string(server.name)
            result.append(member)
        }
        for server in list where server.isGroup && !server.isAuto {
            for member in members(of: server.outbound) ?? [] where seen.insert(key(member)).inserted {
                result.append(member)
            }
        }
        return result
    }

    /// The auto row for a subscription's servers. Nil with fewer than two
    /// servers, or when the provider's own group already covers them all.
    static func auto(for list: [Server]) -> ParsedServer? {
        let all = uniqueMembers(of: list)
        guard all.count >= 2 else { return nil }
        let covered = list.contains { server in
            guard !server.isAuto, let members = members(of: server.outbound) else { return false }
            return Set(members.map(key)).count == all.count
        }
        guard !covered else { return nil }
        for member in all {
            guard let proto = member["type"]?.stringValue.flatMap(ProxyProtocol.init(rawValue:)),
                  let host = member["server"]?.stringValue, let port = member["server_port"]?.intValue
            else { continue }
            let outbound: JSONValue = ["type": .string(type), autoKey: true, "outbounds": .array(all)]
            return ParsedServer(name: autoName, proto: proto, host: host, port: port, outbound: outbound, shareLink: nil)
        }
        return nil
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
    public var isGroup: Bool { ServerGroup.members(of: outbound) != nil }

    /// The row the app added, as opposed to a group from the provider.
    public var isAuto: Bool { outbound[ServerGroup.autoKey]?.boolValue == true }

    /// The servers of a group, each as its own Server; empty for a single server.
    public var members: [Server] {
        (ServerGroup.members(of: outbound) ?? []).compactMap { member in
            guard let type = member["type"]?.stringValue, let proto = ProxyProtocol(rawValue: type),
                  let host = member["server"]?.stringValue, let port = member["server_port"]?.intValue
            else { return nil }
            let name = member["tag"]?.stringValue ?? "\(host):\(port)"
            let parsed = ParsedServer(name: name, proto: proto, host: host, port: port, outbound: member, shareLink: nil)
            return Server(subscriptionID: subscriptionID, parsed: parsed)
        }
    }
}
