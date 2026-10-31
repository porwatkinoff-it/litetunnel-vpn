import Foundation

/// One per app connection. It remembers a change not yet undone, so if the
/// app dies while connected the proxy does not stay pointed at a closed port.
final class Helper: NSObject, ProxyHelperProtocol {
    private let queue = DispatchQueue(label: "litetunnel.proxyhelper")
    private var pending: SystemProxy.Backup?

    func enable(port: Int, bypass: [String], reply: @escaping (Data?, String?) -> Void) {
        queue.async {
            do {
                let backup = try SystemProxy.enable(port: port, bypass: bypass)
                self.pending = backup
                reply(try JSONEncoder().encode(backup), nil)
            } catch {
                reply(nil, error.localizedDescription)
            }
        }
    }

    func restore(_ backup: Data, reply: @escaping (String?) -> Void) {
        queue.async {
            do {
                try SystemProxy.restore(try JSONDecoder().decode(SystemProxy.Backup.self, from: backup))
                self.pending = nil
                reply(nil)
            } catch {
                reply(error.localizedDescription)
            }
        }
    }

    func appWentAway() {
        queue.sync {
            guard let pending else { return }
            try? SystemProxy.restore(pending)
            self.pending = nil
        }
    }
}

final class ListenerDelegate: NSObject, NSXPCListenerDelegate {
    // Only the app that contains this service can reach it.
    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection connection: NSXPCConnection) -> Bool {
        let helper = Helper()
        connection.exportedInterface = NSXPCInterface(with: ProxyHelperProtocol.self)
        connection.exportedObject = helper
        connection.invalidationHandler = { helper.appWentAway() }
        connection.resume()
        return true
    }
}

let delegate = ListenerDelegate()
let listener = NSXPCListener.service()
listener.delegate = delegate
listener.resume()
