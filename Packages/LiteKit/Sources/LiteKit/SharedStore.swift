import Foundation

/// Files the app and the tunnel extension share through the App Group.
///
/// ```
/// <container>/state.json        AppState (written by the app only)
/// <container>/config.json       sing-box config for the selected server
/// <container>/rulesets/*.srs    downloaded rule-sets
/// <container>/command.sock      libbox command socket (created by libbox)
/// <container>/work, tmp         libbox working and temp folders
/// ```
public struct SharedStore: Sendable {
    public let container: URL

    public init(container: URL) {
        self.container = container
    }

    /// The group ID comes from the `LTAppGroup` Info.plist key, set per platform
    /// in the project: iOS uses `group.…`, macOS the team-prefixed form.
    public static func appGroupID(bundle: Bundle = .main) -> String? {
        bundle.object(forInfoDictionaryKey: "LTAppGroup") as? String
    }

    /// Opens the first group the signature allows: the Info.plist one, then
    /// the ones in the embedded profile. A tool that re-signs the IPA with
    /// someone's own profile puts that profile's group in the signature but
    /// leaves the Info.plist value, so without the profile iOS gives no
    /// container. The app and the tunnel read the same profiles in the same
    /// order, so they land in the same group.
    public static func shared(bundle: Bundle = .main) -> SharedStore? {
        for group in candidateGroups(bundle: bundle) {
            if let url = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group) {
                return SharedStore(container: url)
            }
        }
        return nil
    }

    static func candidateGroups(bundle: Bundle) -> [String] {
        var groups: [String] = []
        if let group = appGroupID(bundle: bundle) { groups.append(group) }
        for url in profileURLs(bundle: bundle) {
            guard let data = try? Data(contentsOf: url) else { continue }
            for group in appGroups(inProfile: data) where !groups.contains(group) {
                groups.append(group)
            }
        }
        return groups
    }

    /// The bundle's own profile, and for the tunnel also the app's, since a
    /// signing tool may embed a profile in the app only.
    static func profileURLs(bundle: Bundle) -> [URL] {
        var urls = [bundle.bundleURL.appendingPathComponent("embedded.mobileprovision")]
        if bundle.bundleURL.pathExtension == "appex" {
            let app = bundle.bundleURL.deletingLastPathComponent().deletingLastPathComponent()
            urls.append(app.appendingPathComponent("embedded.mobileprovision"))
        }
        return urls
    }

    /// The App Groups a provisioning profile allows. The profile is a signed
    /// CMS message that holds its plist as plain XML.
    static func appGroups(inProfile data: Data) -> [String] {
        guard let start = data.range(of: Data("<?xml".utf8)),
              let end = data.range(of: Data("</plist>".utf8), in: start.upperBound..<data.endIndex),
              let plist = try? PropertyListSerialization.propertyList(from: Data(data[start.lowerBound..<end.upperBound]), format: nil) as? [String: Any],
              let entitlements = plist["Entitlements"] as? [String: Any],
              let groups = entitlements["com.apple.security.application-groups"] as? [String]
        else { return [] }
        return groups
    }

    public var stateURL: URL { container.appendingPathComponent("state.json") }
    public var configURL: URL { container.appendingPathComponent("config.json") }
    public var ruleSetDirectory: URL { container.appendingPathComponent("rulesets", isDirectory: true) }
    public var workingDirectory: URL { container.appendingPathComponent("work", isDirectory: true) }
    public var tempDirectory: URL { container.appendingPathComponent("tmp", isDirectory: true) }

    public func prepareDirectories() throws {
        for dir in [ruleSetDirectory, workingDirectory, tempDirectory] {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
    }

    /// Missing or unreadable state starts fresh rather than failing the app.
    public func loadState() -> AppState {
        guard let data = try? Data(contentsOf: stateURL),
              let state = try? Self.decoder.decode(AppState.self, from: data)
        else { return AppState() }
        return state
    }

    public func save(_ state: AppState) throws {
        try Self.encoder.encode(state).write(to: stateURL, options: .atomic)
    }

    /// Builds the config for the selected server and writes it for the extension.
    /// Returns false when no server is selected.
    @discardableResult
    public func writeConfig(for state: AppState) throws -> Bool {
        guard let server = state.selectedServer else {
            try? FileManager.default.removeItem(at: configURL)
            return false
        }
        let config = ConfigBuilder.build(server: server, settings: state.settings, ruleSetDirectory: ruleSetDirectory)
        try config.encoded(pretty: false).write(to: configURL, options: .atomic)
        return true
    }

    public func readConfig() throws -> String {
        try String(contentsOf: configURL, encoding: .utf8)
    }

    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()
}

/// Downloads binary rule-sets once a week. Runs in the app, never in the tunnel.
public struct RuleSetUpdater: Sendable {
    public var maxAge: TimeInterval = 7 * 24 * 3600

    public init() {}

    /// Whether any needed rule-set is missing or old.
    public func isDue(_ tags: [String], in directory: URL, now: Date = Date()) -> Bool {
        tags.contains { tag in
            let url = directory.appendingPathComponent("\(tag).srs")
            guard let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            else { return true }
            return now.timeIntervalSince(modified) >= maxAge
        }
    }

    /// Fetches the given rule-sets. Returns the tags that failed.
    public func update(_ tags: [String], into directory: URL) async -> [String] {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.waitsForConnectivity = false
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }

        var failed: [String] = []
        for tag in tags {
            var saved = false
            for url in RuleSets.urls(for: tag) {
                guard let (data, response) = try? await session.data(from: url),
                      (response as? HTTPURLResponse)?.statusCode == 200,
                      RuleSets.looksValid(data)
                else { continue }
                if (try? data.write(to: directory.appendingPathComponent("\(tag).srs"), options: .atomic)) != nil {
                    saved = true
                    break
                }
            }
            if !saved { failed.append(tag) }
        }
        return failed
    }
}
