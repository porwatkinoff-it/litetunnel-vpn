import Foundation

/// Turns the Mac's system proxy on and off through the ProxyHelper service.
///
/// What the helper replaced is kept in a file until it is put back, so a
/// crash or a power cut while connected is undone at the next launch.
///
/// The connection is guarded by `lock`.
final class SystemProxySwitch: @unchecked Sendable {
    private let lock = NSLock()
    private var connection: NSXPCConnection?

    private static let backupURL = FileManager.default
        .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("LiteTunnel", isDirectory: true)
        .appendingPathComponent("proxy-backup.json")

    func enable(port: Int, bypass: [String]) async throws {
        let backup: Data = try await call { helper, done in
            helper.enable(port: port, bypass: bypass) { data, message in
                if let data {
                    done(.success(data))
                } else {
                    done(.failure(VPNError(message ?? "Не удалось включить системный прокси")))
                }
            }
        }
        do {
            try FileManager.default.createDirectory(at: Self.backupURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try backup.write(to: Self.backupURL, options: .atomic)
        } catch {
            // Without the file a crash would leave the proxy on.
            try? await restore(backup)
            throw error
        }
    }

    /// Puts back the settings from the last `enable`, if any are left.
    func restore() async throws {
        guard let backup = try? Data(contentsOf: Self.backupURL) else { return }
        try await restore(backup)
        try? FileManager.default.removeItem(at: Self.backupURL)
    }

    /// The same, waiting on the calling thread: for app quit.
    func restoreNow() {
        guard let backup = try? Data(contentsOf: Self.backupURL) else { return }
        let done = DispatchSemaphore(value: 0)
        let result = Box()
        let helper = remote { _ in done.signal() }
        helper.restore(backup) { message in
            result.ok = message == nil
            done.signal()
        }
        // The password dialog, if one is needed, may take longer; then the
        // file stays and the next launch finishes the job.
        if done.wait(timeout: .now() + 5) == .success, result.ok {
            try? FileManager.default.removeItem(at: Self.backupURL)
        }
    }

    private func restore(_ backup: Data) async throws {
        let _: Void = try await call { helper, done in
            helper.restore(backup) { message in
                done(message.map { .failure(VPNError($0)) } ?? .success(()))
            }
        }
    }

    // MARK: XPC

    private func call<T>(_ body: @escaping (ProxyHelperProtocol, @escaping (Result<T, Error>) -> Void) -> Void) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            // XPC calls either the reply or the error handler; this keeps a
            // stray second answer from resuming twice.
            let once = Once { continuation.resume(with: $0) }
            let helper = remote { error in
                LocalCore.log.error("proxy helper: \(error.localizedDescription, privacy: .public)")
                once.finish(.failure(VPNError("Служба системного прокси не отвечает")))
            }
            body(helper) { once.finish($0) }
        }
    }

    private func remote(_ onError: @escaping (Error) -> Void) -> ProxyHelperProtocol {
        lock.lock()
        defer { lock.unlock() }
        let connection = self.connection ?? {
            let name = ProxyHelperService.name(appBundleID: Bundle.main.bundleIdentifier ?? "com.example.litetunnel")
            let connection = NSXPCConnection(serviceName: name)
            connection.remoteObjectInterface = NSXPCInterface(with: ProxyHelperProtocol.self)
            connection.invalidationHandler = { [weak self] in
                guard let self else { return }
                self.lock.lock()
                self.connection = nil
                self.lock.unlock()
            }
            connection.resume()
            self.connection = connection
            return connection
        }()
        // The interface is fixed above, so the cast always holds.
        return connection.remoteObjectProxyWithErrorHandler(onError) as! ProxyHelperProtocol
    }

    private final class Box {
        var ok = false
    }

    private final class Once<T> {
        private let lock = NSLock()
        private var body: ((Result<T, Error>) -> Void)?

        init(_ body: @escaping (Result<T, Error>) -> Void) { self.body = body }

        func finish(_ result: Result<T, Error>) {
            lock.lock()
            let body = self.body
            self.body = nil
            lock.unlock()
            body?(result)
        }
    }
}
