import Foundation

/// The XPC service that sets the Mac's system proxy for builds without the
/// VPN entitlement. Inside the app's sandbox networksetup can read but not
/// save, so this helper runs outside it and does nothing else.
///
/// Shared by the app and the helper.
@objc(LTProxyHelper)
protocol ProxyHelperProtocol {
    /// Points the HTTP, HTTPS and SOCKS proxies of every enabled network
    /// service with a real interface at 127.0.0.1:`port`, adding `bypass` to
    /// its exceptions. Replies with what was there before, for `restore`, or
    /// with an error text.
    func enable(port: Int, bypass: [String], reply: @escaping (Data?, String?) -> Void)

    /// Puts back what `enable` replaced, wherever it still points at us.
    /// Replies with an error text, or nil.
    func restore(_ backup: Data, reply: @escaping (String?) -> Void)
}

enum ProxyHelperService {
    static func name(appBundleID: String) -> String { appBundleID + ".proxyhelper" }
}
