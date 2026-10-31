import Foundation

enum Base64 {
    /// Decodes standard or URL-safe base64, with or without padding and line breaks.
    static func decode(_ string: String) -> Data? {
        var s = string.filter { !$0.isWhitespace }
        guard !s.isEmpty else { return nil }
        s = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while s.hasSuffix("=") { s.removeLast() }
        switch s.count % 4 {
        case 1: return nil
        case 2: s += "=="
        case 3: s += "="
        default: break
        }
        return Data(base64Encoded: s)
    }

    static func decodeString(_ string: String) -> String? {
        guard let data = decode(string) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
