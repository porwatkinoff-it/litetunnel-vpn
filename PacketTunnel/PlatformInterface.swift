import Foundation
import Libbox
import Network
import NetworkExtension

/// What the core asks of the system: the tun device, the default network,
/// the interface list. Everything a VPN client on Apple platforms does not
/// need (shell, bridge, Wi-Fi state, process lookup) is a stub.
final class PlatformInterface: NSObject, LibboxPlatformInterfaceProtocol {
    private weak var provider: NEPacketTunnelProvider?
    private let allNetworks: Bool
    private let bindOutside: Bool
    private var pathMonitor: NWPathMonitor?
    private var currentPath: Network.NWPath?
    private let monitorQueue = DispatchQueue(label: "litetunnel.path", qos: .utility)
    private let indexLock = NSLock()
    private var outsideIndex: UInt32 = 0

    /// - Parameter bindOutside: for a core the app runs while the VPN may be
    ///   up. Its sockets are bound to the Wi-Fi, cellular or wired interface,
    ///   so they go around the tunnel the way the extension's own do.
    init(provider: NEPacketTunnelProvider?, includeAllNetworks: Bool, bindOutside: Bool = false) {
        self.provider = provider
        self.allNetworks = includeAllNetworks
        self.bindOutside = bindOutside
    }

    func reset() {
        pathMonitor?.cancel()
        pathMonitor = nil
    }

    // MARK: tun

    func openTun(_ options: LibboxTunOptionsProtocol?, ret0_: UnsafeMutablePointer<Int32>?) throws {
        guard let options, let ret0_ else { throw TunnelError("Пустые параметры tun") }
        guard let provider else { throw TunnelError("Туннель уже остановлен") }
        let settings = try Self.networkSettings(options)

        var applyError: Error?
        let done = DispatchSemaphore(value: 0)
        provider.setTunnelNetworkSettings(settings) { error in
            applyError = error
            done.signal()
        }
        done.wait()
        if let applyError { throw applyError }

        let fd = LibboxGetTunnelFileDescriptor()
        guard fd != -1 else { throw TunnelError("Не найден дескриптор utun") }
        ret0_.pointee = fd
    }

    static func networkSettings(_ options: LibboxTunOptionsProtocol) throws -> NEPacketTunnelNetworkSettings {
        // The remote address is only a label for the system; the core picks
        // the real server.
        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: "127.0.0.1")
        guard options.getAutoRoute() else { return settings }
        settings.mtu = NSNumber(value: options.getMTU())

        let dnsServers = Drain.strings(try options.getDNSServerAddress())
        if !dnsServers.isEmpty {
            let dns = NEDNSSettings(servers: dnsServers)
            // Every lookup goes to the tun, where the core answers it.
            dns.matchDomains = [""]
            dns.matchDomainsNoSearch = true
            settings.dnsSettings = dns
        }

        let v4 = Drain.prefixes(options.getInet4Address())
        if !v4.isEmpty {
            let ipv4 = NEIPv4Settings(addresses: v4.map { $0.address() }, subnetMasks: v4.map { $0.mask() })
            let routes = Drain.prefixes(options.getInet4RouteAddress())
                .map { NEIPv4Route(destinationAddress: $0.address(), subnetMask: $0.mask()) }
            ipv4.includedRoutes = routes.isEmpty ? [NEIPv4Route.default()] : routes
            ipv4.excludedRoutes = Drain.prefixes(options.getInet4RouteExcludeAddress())
                .map { NEIPv4Route(destinationAddress: $0.address(), subnetMask: $0.mask()) }
            settings.ipv4Settings = ipv4
        }

        let v6 = Drain.prefixes(options.getInet6Address())
        if !v6.isEmpty {
            let ipv6 = NEIPv6Settings(addresses: v6.map { $0.address() },
                                      networkPrefixLengths: v6.map { NSNumber(value: $0.prefix()) })
            let routes = Drain.prefixes(options.getInet6RouteAddress())
                .map { NEIPv6Route(destinationAddress: $0.address(), networkPrefixLength: NSNumber(value: $0.prefix())) }
            ipv6.includedRoutes = routes.isEmpty ? [NEIPv6Route.default()] : routes
            ipv6.excludedRoutes = Drain.prefixes(options.getInet6RouteExcludeAddress())
                .map { NEIPv6Route(destinationAddress: $0.address(), networkPrefixLength: NSNumber(value: $0.prefix())) }
            settings.ipv6Settings = ipv6
        }
        return settings
    }

    func underNetworkExtension() -> Bool { true }
    func includeAllNetworks() -> Bool { allNetworks }

    // Sockets the extension opens already bypass its own tunnel, so there is
    // nothing to bind. Saying "platform handles it" spares the core an
    // interface lookup on every new connection.
    func usePlatformAutoDetectControl() -> Bool { true }

    func autoDetectControl(_ fd: Int32) throws {
        guard bindOutside else { return }
        indexLock.lock()
        var index = outsideIndex
        indexLock.unlock()
        guard index > 0 else { throw TunnelError("Нет сети") }
        // An unbound socket still reports its family.
        var storage = sockaddr_storage()
        var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let v6 = withUnsafeMutablePointer(to: &storage) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                getsockname(fd, $0, &length) == 0 && $0.pointee.sa_family == sa_family_t(AF_INET6)
            }
        }
        let size = socklen_t(MemoryLayout<UInt32>.size)
        let result = v6 ? setsockopt(fd, IPPROTO_IPV6, IPV6_BOUND_IF, &index, size)
                        : setsockopt(fd, IPPROTO_IP, IP_BOUND_IF, &index, size)
        if result != 0 { throw TunnelError(String(cString: strerror(errno))) }
    }

    // MARK: default network

    func startDefaultInterfaceMonitor(_ listener: LibboxInterfaceUpdateListenerProtocol?) throws {
        guard let listener else { return }
        // utun counts as "other".
        let monitor = bindOutside ? NWPathMonitor(prohibitedInterfaceTypes: [.other]) : NWPathMonitor()
        let ready = DispatchSemaphore(value: 0)
        var first = true
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            self.currentPath = path
            self.indexLock.lock()
            self.outsideIndex = path.availableInterfaces.first.map { UInt32($0.index) } ?? 0
            self.indexLock.unlock()
            Self.report(path, to: listener)
            if first {
                first = false
                ready.signal()
            }
        }
        monitor.start(queue: monitorQueue)
        pathMonitor = monitor
        // The core needs a default interface before it can dial.
        _ = ready.wait(timeout: .now() + 2)
    }

    func closeDefaultInterfaceMonitor(_ listener: LibboxInterfaceUpdateListenerProtocol?) throws {
        reset()
    }

    private static func report(_ path: Network.NWPath, to listener: LibboxInterfaceUpdateListenerProtocol) {
        guard path.status != .unsatisfied, let interface = path.availableInterfaces.first else {
            listener.updateDefaultInterface("", interfaceIndex: -1, isExpensive: false, isConstrained: false)
            return
        }
        listener.updateDefaultInterface(interface.name, interfaceIndex: Int32(interface.index),
                                        isExpensive: path.isExpensive, isConstrained: path.isConstrained)
    }

    func getInterfaces() throws -> LibboxNetworkInterfaceIteratorProtocol {
        guard let path = currentPath ?? pathMonitor?.currentPath else {
            return NetworkInterfaceArrayIterator([])
        }
        let addresses = Self.interfaceAddresses()
        let list: [LibboxNetworkInterface] = path.availableInterfaces.map { nw in
            let item = LibboxNetworkInterface()
            item.name = nw.name
            item.index = Int32(nw.index)
            item.addresses = StringArrayIterator(addresses[nw.name] ?? [])
            switch nw.type {
            case .wifi: item.type = LibboxInterfaceTypeWIFI
            case .cellular: item.type = LibboxInterfaceTypeCellular
            case .wiredEthernet: item.type = LibboxInterfaceTypeEthernet
            default: item.type = LibboxInterfaceTypeOther
            }
            item.metered = path.isExpensive
            return item
        }
        return NetworkInterfaceArrayIterator(list)
    }

    /// Interface name to its addresses in CIDR form, from getifaddrs.
    static func interfaceAddresses() -> [String: [String]] {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [:] }
        defer { freeifaddrs(head) }
        var out: [String: [String]] = [:]
        for entry in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = entry.pointee
            guard let addr = ifa.ifa_addr, let mask = ifa.ifa_netmask else { continue }
            let family = Int32(addr.pointee.sa_family)
            guard family == AF_INET || family == AF_INET6 else { continue }
            guard let ip = numericHost(addr) else { continue }
            out[String(cString: ifa.ifa_name), default: []].append("\(ip)/\(prefixLength(mask, family: family))")
        }
        return out
    }

    private static func numericHost(_ addr: UnsafeMutablePointer<sockaddr>) -> String? {
        var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        let length = socklen_t(addr.pointee.sa_len)
        guard getnameinfo(addr, length, &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 else { return nil }
        // Drop the "%en0" zone of link-local IPv6 addresses.
        return String(cString: host).split(separator: "%").first.map(String.init)
    }

    private static func prefixLength(_ mask: UnsafeMutablePointer<sockaddr>, family: Int32) -> Int {
        let bytes: [UInt8]
        if family == AF_INET {
            bytes = mask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                withUnsafeBytes(of: $0.pointee.sin_addr) { Array($0) }
            }
        } else {
            bytes = mask.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) {
                withUnsafeBytes(of: $0.pointee.sin6_addr) { Array($0) }
            }
        }
        return bytes.reduce(0) { $0 + $1.nonzeroBitCount }
    }

    // MARK: not used on Apple platforms

    func clearDNSCache() {}
    func registerMyInterface(_ name: String?) {}
    func readWIFIState() -> LibboxWIFIState? { nil }
    func localDNSTransport() -> LibboxLocalDNSTransportProtocol? { nil }
    func usePlatformBridge() -> Bool { false }
    func usePlatformShell() -> Bool { false }
    func tailscaleHostname() -> String { "" }

    func findConnectionOwner(_ ipProtocol: Int32, sourceAddress: String?, sourcePort: Int32,
                             destinationAddress: String?, destinationPort: Int32) throws -> LibboxConnectionOwner {
        throw TunnelError("Не поддерживается")
    }

    func send(_ notification: LibboxNotification?) throws {}
    func cancelNotification(_ identifier: String?, typeID: Int32) throws {}

    func startNeighborMonitor(_ listener: LibboxNeighborUpdateListenerProtocol?) throws {}
    func closeNeighborMonitor(_ listener: LibboxNeighborUpdateListenerProtocol?) throws {}

    func createBridge(_ options: LibboxBridgeOptions?) throws -> LibboxBridgeSessionProtocol {
        throw TunnelError("Не поддерживается")
    }

    func checkPlatformShell() throws {
        throw TunnelError("Не поддерживается")
    }

    func lookupUser(_ username: String?) throws -> LibboxPlatformUser {
        throw TunnelError("Не поддерживается")
    }

    func openShellSession(_ user: LibboxPlatformUser?, command: String?, environ: LibboxStringIteratorProtocol?,
                          term: String?, rows: Int32, cols: Int32) throws -> LibboxShellSessionProtocol {
        throw TunnelError("Не поддерживается")
    }

    func lookupSFTPServer(_ error: NSErrorPointer) -> String {
        error?.pointee = TunnelError("Не поддерживается") as NSError
        return ""
    }

    func readSystemSSHHostKey(_ error: NSErrorPointer) -> String {
        error?.pointee = TunnelError("Не поддерживается") as NSError
        return ""
    }
}
