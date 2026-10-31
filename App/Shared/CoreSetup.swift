import Darwin
import Foundation
import Libbox
import LiteKit

/// What every core the app runs needs first: the Mac's local proxy and the
/// ping probe on both platforms.
enum CoreSetup {
    private static let lock = NSLock()
    private static var didSetup = false

    /// Libbox takes its folders once per process; later calls do nothing.
    static func ensure(_ store: SharedStore) throws {
        lock.lock()
        defer { lock.unlock() }
        guard !didSetup else { return }
        try store.prepareDirectories()
        let options = LibboxSetupOptions()
        options.basePath = store.container.path
        options.workingPath = store.workingDirectory.path
        options.tempPath = store.tempDirectory.path
        #if os(macOS)
        options.logMaxLines = StatusReader.maxLogLines
        #endif
        var error: NSError?
        LibboxSetup(options, &error)
        if let error { throw error }
        didSetup = true
    }

    /// `count` ports nothing on this device listens on, from the system.
    /// All stay open until the last is found, so none repeats.
    static func freePorts(_ count: Int) throws -> [Int] {
        var sockets: [Int32] = []
        defer { sockets.forEach { Darwin.close($0) } }
        var ports: [Int] = []
        for _ in 0..<count {
            let fd = socket(AF_INET, SOCK_STREAM, 0)
            guard fd >= 0 else { throw VPNError("Не удалось открыть локальный порт") }
            sockets.append(fd)
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_addr.s_addr = inet_addr("127.0.0.1")
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let found = withUnsafeMutablePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.bind(fd, $0, length) == 0 && getsockname(fd, $0, &length) == 0
                }
            }
            guard found else { throw VPNError("Не удалось открыть локальный порт") }
            ports.append(Int(UInt16(bigEndian: address.sin_port)))
        }
        return ports
    }
}

/// The tunnel's files built into the app use this name.
typealias TunnelError = VPNError
