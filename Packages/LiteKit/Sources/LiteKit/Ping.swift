import Darwin
import Foundation
import Network

/// Measures how long it takes to reach a server, outside the tunnel.
///
/// TCP servers get a TCP handshake. Hysteria2 and TUIC run over QUIC, so they
/// get a QUIC handshake instead; with salamander obfs the server does not
/// answer plain QUIC, and the result is nil (not measurable). ICMP sends one
/// echo request. Through the server needs the core: the app runs one for
/// the measurement, times `request(through:)` against it and then runs
/// `check(through:)`.
public enum Ping {
    public static func measure(_ server: Server, mode: PingMode = .tcp, timeout: TimeInterval = 3) async -> PingResult? {
        let members = server.members
        if !members.isEmpty {
            // A group is as fast as its fastest member: the one the core picks.
            var results: [PingResult?] = []
            await withTaskGroup(of: PingResult?.self) { group in
                for member in members { group.addTask { await measure(member, mode: mode, timeout: timeout) } }
                for await result in group { results.append(result) }
            }
            return fastest(results)
        }
        if mode == .icmp {
            let interface = await outsideInterface()
            return await icmp(server.host, interface: interface, timeout: timeout)
        }
        let parameters: NWParameters
        switch server.proto {
        case .hysteria2, .tuic:
            if server.outbound["obfs"] != nil { return nil }
            let alpn = server.outbound["tls"]?["alpn"]?.arrayValue?.compactMap(\.stringValue) ?? []
            let quic = NWProtocolQUIC.Options(alpn: alpn.isEmpty ? ["h3"] : alpn)
            let sni = server.outbound["tls"]?["server_name"]?.stringValue ?? server.host
            sec_protocol_options_set_tls_server_name(quic.securityProtocolOptions, sni)
            // Reachability only: the certificate is checked by the core on connect.
            sec_protocol_options_set_verify_block(quic.securityProtocolOptions, { _, _, complete in complete(true) }, .global())
            parameters = NWParameters(quic: quic)
        default:
            let tcp = NWProtocolTCP.Options()
            tcp.connectionTimeout = Int(timeout.rounded(.up))
            tcp.noDelay = true
            parameters = NWParameters(tls: nil, tcp: tcp)
        }
        // utun counts as "other": this keeps the probe off our own tunnel.
        parameters.prohibitedInterfaceTypes = [.other]
        parameters.serviceClass = .responsiveData

        guard let port = NWEndpoint.Port(rawValue: UInt16(clamping: server.port)) else { return .failed }
        let connection = NWConnection(host: NWEndpoint.Host(server.host), port: port, using: parameters)
        return await withCheckedContinuation { continuation in
            let once = Once(continuation)
            let start = DispatchTime.now()
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.finish(.ms(milliseconds(since: start)))
                    connection.cancel()
                case .failed, .waiting:
                    once.finish(.failed)
                    connection.cancel()
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .utility))
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                once.finish(.failed)
                connection.cancel()
            }
        }
    }

    /// Pings many servers, at most `concurrency` at a time.
    public static func measureAll(_ servers: [Server], mode: PingMode = .tcp, concurrency: Int = 8,
                                  onResult: @escaping @Sendable (UUID, PingResult?) async -> Void) async {
        await forEach(servers, concurrency: concurrency) { server in
            await onResult(server.id, await measure(server, mode: mode))
        }
    }

    /// The best of several results: the lowest time, else failed, else nil.
    public static func fastest(_ results: [PingResult?]) -> PingResult? {
        results.compactMap { $0 }.min { $0.sortValue < $1.sortValue }
    }

    /// Runs `body` for each item, at most `concurrency` at a time.
    public static func forEach<T: Sendable>(_ items: [T], concurrency: Int,
                                            _ body: @escaping @Sendable (T) async -> Void) async {
        await withTaskGroup(of: Void.self) { group in
            var iterator = items.makeIterator()
            func next() -> Bool {
                guard let item = iterator.next() else { return false }
                group.addTask { await body(item) }
                return true
            }
            for _ in 0..<concurrency where !next() { break }
            while await group.next() != nil { _ = next() }
        }
    }

    // MARK: through the proxy

    /// What the check downloads through each server: the start of a large
    /// file on Google's download servers, asked for by range. Some networks
    /// let a connection through, then stall it after about 16 KB. A server
    /// behind one answers a short request in time but cannot load a page.
    public static let checkURL = URL(string: "https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb")!
    public static let checkBytes = 64 * 1024

    /// Times one request to `url` through the HTTP proxy on 127.0.0.1:`port`,
    /// up to the response headers. Nil when it fails or the answer is not the
    /// one the address gives (see `passes`).
    public static func request(_ url: URL, through port: Int, head: Bool, timeout: TimeInterval = 5) async -> Int? {
        let session = proxySession(port, idle: timeout, total: timeout)
        defer { session.invalidateAndCancel() }

        var request = URLRequest(url: url)
        request.httpMethod = head ? "HEAD" : "GET"
        let start = DispatchTime.now()
        guard let (bytes, response) = try? await session.bytes(for: request),
              let status = (response as? HTTPURLResponse)?.statusCode
        else { return nil }
        let ms = milliseconds(since: start)
        bytes.task.cancel()
        return passes(status, from: url) ? ms : nil
    }

    /// Whether a status is the test address's real answer. A generate_204
    /// address answers 204 and nothing else, so a stub page or a login page
    /// on the way fails; any other address passes with 200 to 399.
    public static func passes(_ status: Int, from url: URL) -> Bool {
        url.path.hasSuffix("generate_204") ? status == 204 : (200..<400).contains(status)
    }

    /// Downloads `checkBytes` of `url` through the proxy on 127.0.0.1:`port`.
    /// True once all of them are in. A connection that stalls for `idle`
    /// seconds, or takes longer than `total`, fails.
    public static func check(through port: Int, url: URL = checkURL,
                             idle: TimeInterval = 5, total: TimeInterval = 8) async -> Bool {
        let session = proxySession(port, idle: idle, total: total)
        defer { session.invalidateAndCancel() }

        var request = URLRequest(url: url)
        request.setValue("bytes=0-\(checkBytes - 1)", forHTTPHeaderField: "Range")
        guard let (bytes, response) = try? await session.bytes(for: request),
              let status = (response as? HTTPURLResponse)?.statusCode
        else { return false }
        defer { bytes.task.cancel() }
        guard status == 200 || status == 206 else { return false }
        // Counted, not read whole: a server that ignores the range does not
        // send the whole file.
        var count = 0
        do {
            for try await _ in bytes {
                count += 1
                if count == checkBytes { return true }
            }
        } catch {
            return false
        }
        return false
    }

    private static func proxySession(_ port: Int, idle: TimeInterval, total: TimeInterval) -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.connectionProxyDictionary = [
            "HTTPEnable": true, "HTTPProxy": "127.0.0.1", "HTTPPort": port,
            "HTTPSEnable": true, "HTTPSProxy": "127.0.0.1", "HTTPSPort": port,
        ]
        configuration.timeoutIntervalForRequest = idle
        configuration.timeoutIntervalForResource = total
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpMaximumConnectionsPerHost = 1
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }

    /// What a proxied request time shows as in `mode`.
    public static func result(_ ms: Int?, mode: PingMode) -> PingResult {
        guard let ms else { return .failed }
        guard mode == .real else { return .ms(ms) }
        return .ms(max(1, Int((Double(ms) / 3.3).rounded())))
    }

    // MARK: ICMP

    /// One echo request on a datagram ICMP socket, which needs no privileges
    /// on Apple platforms. Bound to `interface` so it goes around the tunnel.
    static func icmp(_ host: String, interface: UInt32, timeout: TimeInterval) async -> PingResult {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                continuation.resume(returning: icmpBlocking(host, interface: interface, timeout: timeout))
            }
        }
    }

    private static func icmpBlocking(_ host: String, interface: UInt32, timeout: TimeInterval) -> PingResult {
        let deadline = DispatchTime.now() + timeout
        guard var address = resolve(host) else { return .failed }
        let v6 = address.ss_family == sa_family_t(AF_INET6)
        let fd = socket(v6 ? AF_INET6 : AF_INET, SOCK_DGRAM, v6 ? IPPROTO_ICMPV6 : IPPROTO_ICMP)
        guard fd >= 0 else { return .failed }
        defer { close(fd) }
        if interface > 0 {
            var index = interface
            let size = socklen_t(MemoryLayout<UInt32>.size)
            _ = v6 ? setsockopt(fd, IPPROTO_IPV6, IPV6_BOUND_IF, &index, size)
                   : setsockopt(fd, IPPROTO_IP, IP_BOUND_IF, &index, size)
        }

        let id = UInt16.random(in: 1...UInt16.max)
        let sequence: UInt16 = 1
        var packet = [UInt8](repeating: 0, count: 16)
        packet[0] = v6 ? 128 : 8
        packet[4] = UInt8(id >> 8); packet[5] = UInt8(id & 0xff)
        packet[6] = UInt8(sequence >> 8); packet[7] = UInt8(sequence & 0xff)
        // The kernel fills in the ICMPv6 checksum, not the ICMP one.
        if !v6 {
            let sum = checksum(packet)
            packet[2] = UInt8(sum >> 8); packet[3] = UInt8(sum & 0xff)
        }

        let start = DispatchTime.now()
        let sent = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                sendto(fd, packet, packet.count, 0, $0, socklen_t($0.pointee.sa_len))
            }
        }
        guard sent == packet.count else { return .failed }

        var buffer = [UInt8](repeating: 0, count: 1500)
        while true {
            let left = Int(deadline.uptimeNanoseconds) - Int(DispatchTime.now().uptimeNanoseconds)
            guard left > 0 else { return .failed }
            var poller = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            guard poll(&poller, 1, Int32(left / 1_000_000) + 1) > 0 else { return .failed }
            let count = recv(fd, &buffer, buffer.count, 0)
            guard count > 0 else { return .failed }
            // IPv4 replies come with their IP header.
            var offset = 0
            if !v6, buffer[0] >> 4 == 4 { offset = Int(buffer[0] & 0x0f) * 4 }
            guard count >= offset + 8 else { continue }
            let type = buffer[offset]
            let replyID = UInt16(buffer[offset + 4]) << 8 | UInt16(buffer[offset + 5])
            let replySequence = UInt16(buffer[offset + 6]) << 8 | UInt16(buffer[offset + 7])
            // Every ICMP socket sees every reply: keep only the answer to ours.
            if type == (v6 ? 129 : 0), replyID == id, replySequence == sequence {
                return .ms(milliseconds(since: start))
            }
        }
    }

    /// The first IPv4 address of `host`, else its first IPv6 one.
    private static func resolve(_ host: String) -> sockaddr_storage? {
        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        hints.ai_socktype = SOCK_DGRAM
        var list: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &list) == 0, let first = list else { return nil }
        defer { freeaddrinfo(list) }
        let entries = Array(sequence(first: first) { $0.pointee.ai_next })
        guard let pick = entries.first(where: { $0.pointee.ai_family == AF_INET })
            ?? entries.first(where: { $0.pointee.ai_family == AF_INET6 }),
            let addr = pick.pointee.ai_addr
        else { return nil }
        var storage = sockaddr_storage()
        withUnsafeMutableBytes(of: &storage) { raw in
            raw.copyMemory(from: UnsafeRawBufferPointer(start: addr, count: Int(pick.pointee.ai_addrlen)))
        }
        return storage
    }

    static func checksum(_ bytes: [UInt8]) -> UInt16 {
        var sum: UInt32 = 0
        var i = 0
        while i + 1 < bytes.count {
            sum += UInt32(bytes[i]) << 8 | UInt32(bytes[i + 1])
            i += 2
        }
        if i < bytes.count { sum += UInt32(bytes[i]) << 8 }
        while sum >> 16 != 0 { sum = (sum & 0xffff) + (sum >> 16) }
        return ~UInt16(sum)
    }

    /// The Wi-Fi, cellular or wired interface, 0 when there is none. utun
    /// counts as "other".
    public static func outsideInterface() async -> UInt32 {
        await withCheckedContinuation { continuation in
            let monitor = NWPathMonitor(prohibitedInterfaceTypes: [.other])
            let once = Once(continuation)
            monitor.pathUpdateHandler = { path in
                once.finish(path.availableInterfaces.first.map { UInt32($0.index) } ?? 0)
                monitor.cancel()
            }
            monitor.start(queue: .global(qos: .utility))
        }
    }

    private static func milliseconds(since start: DispatchTime) -> Int {
        max(1, Int((DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1_000_000))
    }

    private final class Once<T>: @unchecked Sendable {
        private var continuation: CheckedContinuation<T, Never>?
        private let lock = NSLock()

        init(_ continuation: CheckedContinuation<T, Never>) { self.continuation = continuation }

        func finish(_ result: T) {
            lock.lock()
            let c = continuation
            continuation = nil
            lock.unlock()
            c?.resume(returning: result)
        }
    }
}
