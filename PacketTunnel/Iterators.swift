import Foundation
import Libbox

/// Swift arrays handed to the core as gomobile iterators.
final class StringArrayIterator: NSObject, LibboxStringIteratorProtocol {
    private let values: [String]
    private var index = 0

    init(_ values: [String]) { self.values = values }

    func hasNext() -> Bool { index < values.count }
    func len() -> Int32 { Int32(values.count) }

    func next() -> String {
        defer { index += 1 }
        return values[index]
    }
}

final class NetworkInterfaceArrayIterator: NSObject, LibboxNetworkInterfaceIteratorProtocol {
    private let values: [LibboxNetworkInterface]
    private var index = 0

    init(_ values: [LibboxNetworkInterface]) { self.values = values }

    func hasNext() -> Bool { index < values.count }

    func next() -> LibboxNetworkInterface? {
        defer { index += 1 }
        return values[index]
    }
}

/// Iterators from the core, read into arrays.
enum Drain {
    static func strings(_ iterator: LibboxStringIteratorProtocol?) -> [String] {
        guard let iterator else { return [] }
        var out: [String] = []
        while iterator.hasNext() { out.append(iterator.next()) }
        return out
    }

    static func prefixes(_ iterator: LibboxRoutePrefixIteratorProtocol?) -> [LibboxRoutePrefix] {
        guard let iterator else { return [] }
        var out: [LibboxRoutePrefix] = []
        while iterator.hasNext() {
            if let prefix = iterator.next() { out.append(prefix) }
        }
        return out
    }
}
