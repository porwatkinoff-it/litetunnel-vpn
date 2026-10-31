import Foundation
import SystemConfiguration

/// Reads proxy settings through SystemConfiguration, which needs no rights,
/// and changes them with networksetup, which an administrator may run without
/// a password. Anyone else gets the system's administrator dialog.
///
/// Only enabled services with a real interface (Wi-Fi, Ethernet, USB) are
/// touched. VPN services of other apps have none and stay as they are.
enum SystemProxy {
    static let host = "127.0.0.1"
    static let tool = "/usr/sbin/networksetup"

    struct Proxy: Codable, Equatable {
        var enabled = false
        var host = ""
        var port = 0
    }

    enum Kind: CaseIterable {
        case web, secureWeb, socks

        var keys: (enable: String, host: String, port: String) {
            switch self {
            case .web: return ("HTTPEnable", "HTTPProxy", "HTTPPort")
            case .secureWeb: return ("HTTPSEnable", "HTTPSProxy", "HTTPSPort")
            case .socks: return ("SOCKSEnable", "SOCKSProxy", "SOCKSPort")
            }
        }

        /// As in `networksetup -setwebproxy` and `-setwebproxystate`.
        var verb: String {
            switch self {
            case .web: return "webproxy"
            case .secureWeb: return "securewebproxy"
            case .socks: return "socksfirewallproxy"
            }
        }
    }

    struct Service: Codable {
        var name: String
        var web = Proxy()
        var secureWeb = Proxy()
        var socks = Proxy()
        var bypass: [String] = []

        subscript(kind: Kind) -> Proxy {
            get {
                switch kind {
                case .web: return web
                case .secureWeb: return secureWeb
                case .socks: return socks
                }
            }
            set {
                switch kind {
                case .web: web = newValue
                case .secureWeb: secureWeb = newValue
                case .socks: socks = newValue
                }
            }
        }
    }

    /// What `enable` replaced, so `restore` can put it back.
    struct Backup: Codable {
        struct Entry: Codable {
            var before: Service
            /// The exceptions list `enable` set.
            var bypass: [String]
        }
        var port: Int
        var entries: [Entry]
    }

    static func enable(port: Int, bypass: [String]) throws -> Backup {
        let services = try current()
        guard !services.isEmpty else { throw HelperError("Нет активных сетевых подключений") }
        var backup = Backup(port: port, entries: [])
        var commands: [[String]] = []
        for service in services {
            for kind in Kind.allCases {
                commands.append(["-set\(kind.verb)", service.name, host, String(port)])
            }
            // The user's own exceptions stay.
            var exceptions = service.bypass
            for item in bypass where !exceptions.contains(item) { exceptions.append(item) }
            commands.append(["-setproxybypassdomains", service.name] + exceptions)
            backup.entries.append(.init(before: service, bypass: exceptions))
        }
        do {
            try run(commands)
        } catch {
            // Undo whatever part did go through.
            try? restore(backup)
            throw error
        }
        return backup
    }

    static func restore(_ backup: Backup) throws {
        var now: [String: Service] = [:]
        for service in try current() { now[service.name] = service }
        var commands: [[String]] = []
        for entry in backup.entries {
            let before = entry.before
            guard let service = now[before.name] else { continue }
            for kind in Kind.allCases {
                // Changed since by the user or another app: theirs now.
                let proxy = service[kind]
                guard proxy.enabled, proxy.host == host, proxy.port == backup.port else { continue }
                let old = before[kind]
                let hadServer = !old.host.isEmpty && old.port > 0
                if hadServer {
                    commands.append(["-set\(kind.verb)", before.name, old.host, String(old.port)])
                }
                if !old.enabled || !hadServer {
                    commands.append(["-set\(kind.verb)state", before.name, "off"])
                }
            }
            if service.bypass == entry.bypass {
                commands.append(["-setproxybypassdomains", before.name] + (before.bypass.isEmpty ? ["Empty"] : before.bypass))
            }
        }
        try run(commands)
    }

    /// Enabled services on a real interface, with their proxy settings now,
    /// in the system's order.
    static func current() throws -> [Service] {
        guard let prefs = SCPreferencesCreate(nil, "LiteTunnel" as CFString, nil),
              let set = SCNetworkSetCopyCurrent(prefs),
              let services = SCNetworkSetCopyServices(set) as? [SCNetworkService]
        else { throw HelperError("Не удалось прочитать настройки сети") }
        let names = try serviceNames()

        return services.compactMap { service -> Service? in
            guard SCNetworkServiceGetEnabled(service),
                  let interface = SCNetworkServiceGetInterface(service),
                  SCNetworkInterfaceGetBSDName(interface) != nil,
                  let name = SCNetworkServiceGetName(service) as String?,
                  names.contains(name)
            else { return nil }
            let config = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeProxies)
                .flatMap(SCNetworkProtocolGetConfiguration) as? [String: Any] ?? [:]
            var item = Service(name: name)
            for kind in Kind.allCases {
                let keys = kind.keys
                item[kind] = Proxy(enabled: (config[keys.enable] as? Int ?? 0) != 0,
                                   host: config[keys.host] as? String ?? "",
                                   port: config[keys.port] as? Int ?? 0)
            }
            item.bypass = config["ExceptionsList"] as? [String] ?? []
            return item
        }
        .sorted { names.firstIndex(of: $0.name) ?? 0 < names.firstIndex(of: $1.name) ?? 0 }
    }

    // MARK: networksetup

    /// The enabled services networksetup knows by name, in the system's
    /// order. The system also keeps hidden ones (such as "Ethernet Adapter
    /// (en5)") that networksetup refuses.
    private static func serviceNames() throws -> [String] {
        let (status, output) = execute(tool, ["-listallnetworkservices"])
        guard status == 0 else { throw HelperError("networksetup -listallnetworkservices: \(output)") }
        // The first line explains that "*" marks a disabled service.
        return output.split(separator: "\n").dropFirst()
            .map(String.init)
            .filter { !$0.hasPrefix("*") }
    }

    /// Runs networksetup once per command; it prints nothing when a change
    /// goes through. If it asks for an administrator, the rest of the
    /// commands go through the system's password dialog in one go.
    static func run(_ commands: [[String]]) throws {
        for (index, command) in commands.enumerated() {
            let (status, output) = execute(tool, command)
            if status == 0 && output.isEmpty { continue }
            if output.contains("admin") {
                try runAsAdministrator(Array(commands[index...]))
                return
            }
            throw HelperError("networksetup \(command[0]): \(output.isEmpty ? "код \(status)" : output)")
        }
    }

    private static func runAsAdministrator(_ commands: [[String]]) throws {
        let shell = commands
            .map { ([tool] + $0).map(shellQuoted).joined(separator: " ") }
            .joined(separator: " && ")
        let script = "do shell script \(appleScriptQuoted(shell)) with prompt \(appleScriptQuoted("LiteTunnel хочет изменить настройки прокси.")) with administrator privileges"
        let (status, output) = execute("/usr/bin/osascript", ["-e", script])
        guard status == 0 else {
            // -128: the user pressed Cancel.
            if output.contains("-128") { throw HelperError("Настройки прокси не изменены: нужен пароль администратора") }
            throw HelperError("Не удалось изменить настройки прокси: \(output)")
        }
    }

    private static func execute(_ path: String, _ arguments: [String]) -> (Int32, String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do {
            try process.run()
        } catch {
            return (-1, error.localizedDescription)
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let output = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return (process.terminationStatus, output)
    }

    private static func shellQuoted(_ text: String) -> String {
        "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func appleScriptQuoted(_ text: String) -> String {
        "\"" + text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}

struct HelperError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
