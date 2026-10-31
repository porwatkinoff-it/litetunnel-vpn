import Foundation
#if os(iOS)
import Security
#endif

/// What a subscription request says about this device. Panels with a device
/// limit (Remnawave and others) count devices by `x-hwid` and give clients
/// that do not send it a placeholder server instead of the list.
/// https://docs.rw/docs/features/hwid-device-limit
public enum DeviceInfo {
    /// Random, made once and kept: in the Keychain on iOS, so a reinstall does
    /// not take another of the subscription's device slots; in preferences on macOS.
    /// A UUID fits the panel's rule (10-64 characters of A-Z, a-z, 0-9, `=`, `-`).
    public static let hwid: String = loadOrCreateID()

    public static var headers: [String: String] {
        ["x-hwid": hwid, "x-device-os": os, "x-ver-os": osVersion, "x-device-model": model]
    }

    static var os: String {
        #if os(iOS)
        "iOS"
        #else
        "macOS"
        #endif
    }

    static var osVersion: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return v.patchVersion > 0 ? "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)" : "\(v.majorVersion).\(v.minorVersion)"
    }

    /// "iPhone18,4" or "Mac15,6".
    static var model: String {
        #if os(iOS)
        sysctlString("hw.machine") ?? "iPhone"
        #else
        sysctlString("hw.model") ?? "Mac"
        #endif
    }

    private static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buffer = [UInt8](repeating: 0, count: size)
        guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return nil }
        let value = String(decoding: buffer.prefix { $0 != 0 }, as: UTF8.self)
        return value.isEmpty ? nil : value
    }

    private static let defaultsKey = "LiteTunnel.hwid"

    private static func loadOrCreateID() -> String {
        if let saved = keychainRead() { return saved }
        let id = UserDefaults.standard.string(forKey: defaultsKey) ?? UUID().uuidString
        UserDefaults.standard.set(id, forKey: defaultsKey)
        keychainWrite(id)
        return id
    }

    #if os(iOS)
    private static let keychainQuery: [String: Any] = [
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: "LiteTunnel",
        kSecAttrAccount as String: "hwid",
    ]

    private static func keychainRead() -> String? {
        var query = keychainQuery
        query[kSecReturnData as String] = true
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private static func keychainWrite(_ id: String) {
        var query = keychainQuery
        query[kSecValueData as String] = Data(id.utf8)
        // Never migrates to another phone through a backup: it names this one.
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(query as CFDictionary, nil)
    }
    #else
    private static func keychainRead() -> String? { nil }
    private static func keychainWrite(_ id: String) {}
    #endif
}
