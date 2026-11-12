import Foundation
import LiteKit
import SwiftUI

/// The app's state: servers, subscriptions and settings, saved to the App
/// Group where the tunnel reads its config. Without the group the app keeps
/// them in its own folder and hands them to the tunnel (`TunnelHandoff`).
///
/// Nothing here runs on a timer. Subscriptions update when asked or when the
/// system grants a background refresh, never just because the app opened;
/// rule-sets once a week, and pings only when asked.
@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var state: AppState
    @Published private(set) var refreshing: Set<UUID> = []
    @Published private(set) var pinging = false
    /// The subscription whose header ping button started the current ping.
    @Published private(set) var pingingSubscription: UUID?
    /// The rows being measured: they show a spinner, not their last result.
    @Published private(set) var pingingIDs: Set<UUID> = []
    /// The servers being measured whose results are not in yet.
    private var pendingPings: Set<UUID> = []
    @Published private(set) var updatingRuleSets = false
    @Published var banner: Banner?

    let store: SharedStore
    let vpn: VPNController
    /// The App Group `store` is in; nil when the signature has none.
    let appGroup: String?
    var hasAppGroup: Bool { appGroup != nil }

    struct Banner: Identifiable, Equatable {
        let id = UUID()
        let text: String
        let isError: Bool
    }

    init(vpn: VPNController) {
        self.vpn = vpn
        if let opened = SharedStore.openGroup() {
            store = opened.store
            appGroup = opened.group
            Self.adoptLocalState(into: opened.store)
        } else {
            // Unsigned builds, and some re-signed ones, have no App Group.
            // The tunnel cannot read this folder, so it gets the state with
            // every start and reload instead.
            store = SharedStore.local
            appGroup = nil
        }
        try? store.prepareDirectories()
        state = store.loadState()
        if state.syncAutoGroups() { try? store.save(state) }
        vpn.handoff = { [weak self] in
            guard let self else { return nil }
            return self.store.handoff(for: self.state, appGroup: self.appGroup)
        }
    }

    /// An earlier run that found no App Group kept its subscriptions in the
    /// app's own folder. Moves them into the group when they are newer than
    /// what the group holds, so they are not lost once the group opens.
    private static func adoptLocalState(into shared: SharedStore) {
        let fm = FileManager.default
        let local = SharedStore.local.stateURL
        func modified(_ url: URL) -> Date? {
            (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        }
        guard let localDate = modified(local), localDate > modified(shared.stateURL) ?? .distantPast else { return }
        try? fm.createDirectory(at: shared.container, withIntermediateDirectories: true)
        try? fm.removeItem(at: shared.stateURL)
        try? fm.moveItem(at: local, to: shared.stateURL)
    }

    var settings: AppSettings { state.settings }

    func show(_ text: String, error: Bool = false) {
        banner = Banner(text: text, isError: error)
    }

    // MARK: saving

    /// Applies a change, saves it and, when the config changed while the
    /// tunnel is up, reloads the core in place.
    private func commit(_ change: (inout AppState) -> Void) {
        change(&state)
        persist()
        applyConfig()
    }

    private func persist() {
        do {
            try store.save(state)
        } catch {
            show("Не удалось сохранить: \(error.localizedDescription)", error: true)
        }
    }

    private func applyConfig() {
        let before = try? store.readConfig()
        do {
            try store.writeConfig(for: state)
        } catch {
            show("Не удалось записать конфигурацию: \(error.localizedDescription)", error: true)
            return
        }
        let after = try? store.readConfig()
        guard before != after, vpn.isActive else { return }
        if after == nil {
            // No server left to run.
            vpn.disconnect()
            return
        }
        Task {
            if let problem = await vpn.reload() { show(problem, error: true) }
        }
    }

    // MARK: connection

    func toggleConnection() async {
        if vpn.isActive {
            vpn.disconnect()
            return
        }
        guard state.selectedServer != nil else {
            show("Сначала выберите сервер", error: true)
            return
        }
        applyConfig()
        do {
            try await vpn.connect(settings: state.settings)
        } catch {
            show(error.localizedDescription, error: true)
        }
    }

    func select(_ id: UUID) {
        guard state.selectedServerID != id else { return }
        commit { $0.selectedServerID = id }
    }

    // MARK: settings

    func setSettings(_ new: AppSettings) {
        let old = state.settings
        guard old != new else { return }
        commit { $0.settings = new }

        if old.killSwitch != new.killSwitch || old.excludeLocalNetworks != new.excludeLocalNetworks {
            // These live in the system VPN profile, not in the core's config.
            Task {
                do {
                    try await vpn.applyProfile(settings: new, restartIfActive: true)
                } catch {
                    show(error.localizedDescription, error: true)
                }
            }
        }
        if RuleSets.needed(for: old) != RuleSets.needed(for: new) {
            Task { await updateRuleSets(force: false) }
        }
    }

    /// A binding for settings screens: every change saves at once.
    var settingsBinding: Binding<AppSettings> {
        Binding(get: { self.state.settings }, set: { self.setSettings($0) })
    }

    // MARK: import

    /// Pasted text, a scanned QR code or an opened link.
    @discardableResult
    func importText(_ text: String) async -> Bool {
        await handle(Importer.classify(text))
    }

    func open(_ url: URL) async {
        await handle(Importer.deepLink(url))
    }

    @discardableResult
    private func handle(_ item: ImportItem) async -> Bool {
        switch item {
        case .subscription(let url, let name):
            if let existing = state.subscriptions.first(where: { $0.url == url }) {
                show("Эта подписка уже добавлена, обновляю")
                await refresh(existing.id)
                return true
            }
            let sub = Subscription(name: name ?? url.host ?? "Подписка", url: url)
            commit { $0.subscriptions.append(sub) }
            await refresh(sub.id)
            return true
        case .servers(let list, let skipped):
            var added: [Server] = []
            commit { added = $0.addManual(list) }
            var text = added.isEmpty ? "Эти серверы уже есть" : "Добавлено серверов: \(added.count)"
            if !skipped.isEmpty { text += ", пропущено: \(skipped.count)" }
            show(text, error: added.isEmpty)
            return !added.isEmpty
        case .nothing(let reason):
            show(reason, error: true)
            return false
        }
    }

    // MARK: servers and subscriptions

    func removeServer(_ id: UUID) {
        commit { $0.removeServer(id) }
    }

    func removeSubscription(_ id: UUID) {
        commit { $0.removeSubscription(id) }
    }

    func updateSubscription(_ sub: Subscription) {
        commit { state in
            guard let index = state.subscriptions.firstIndex(where: { $0.id == sub.id }) else { return }
            state.subscriptions[index] = sub
        }
    }

    func moveServers(in subscriptionID: UUID?, from source: IndexSet, to destination: Int) {
        commit { state in
            var block = state.servers(in: subscriptionID)
            block.move(fromOffsets: source, toOffset: destination)
            var iterator = block.makeIterator()
            state.servers = state.servers.map { $0.subscriptionID == subscriptionID ? iterator.next()! : $0 }
        }
    }

    /// `ping` lets a caller that pings everything next skip the per-subscription one.
    func refresh(_ id: UUID, ping: Bool = true) async {
        guard let sub = state.subscriptions.first(where: { $0.id == id }), !refreshing.contains(id) else { return }
        refreshing.insert(id)
        do {
            let result = try await SubscriptionClient().fetch(sub)
            commit { $0.apply(result, to: id) }
            refreshing.remove(id)
            if ping && state.settings.pingOnUpdate { await self.ping(subscription: id) }
        } catch {
            refreshing.remove(id)
            commit { state in
                guard let index = state.subscriptions.firstIndex(where: { $0.id == id }) else { return }
                state.subscriptions[index].lastError = error.localizedDescription
            }
            show("\(sub.name): \(error.localizedDescription)", error: true)
        }
    }

    /// Subscriptions whose interval has passed. Called from background refresh.
    func refreshDue(ping: Bool = true) async {
        let now = Date()
        let due = state.subscriptions.filter { $0.isDue(at: now) }.map(\.id)
        for id in due { await refresh(id, ping: ping) }
    }

    func refreshAll() async {
        for sub in state.subscriptions { await refresh(sub.id) }
    }

    /// When background refresh should run next: the earliest subscription due.
    var nextRefreshDate: Date {
        let dates = state.subscriptions.map { sub in
            (sub.lastUpdated ?? .distantPast).addingTimeInterval(Double(max(1, sub.updateIntervalHours)) * 3600)
        }
        let earliest = dates.min() ?? Date().addingTimeInterval(24 * 3600)
        // Never sooner than an hour: the system would not honour it anyway.
        return max(earliest, Date().addingTimeInterval(3600))
    }

    // MARK: ping

    func ping(_ rows: [Server]) async {
        let servers = state.pingTargets(rows)
        guard !pinging, !servers.isEmpty else { return }
        pinging = true
        pendingPings = Set(servers.map(\.id))
        pingingIDs = state.rowsShowing(pendingPings)
        defer {
            pinging = false
            pendingPings = []
            pingingIDs = []
        }
        let onResult: @Sendable (UUID, PingResult?) async -> Void = { id, result in
            await MainActor.run {
                self.state.setPing(result, for: id)
                self.pendingPings.remove(id)
                self.pingingIDs = self.state.rowsShowing(self.pendingPings)
            }
        }
        do {
            try await ProxyProbe.measure(servers, settings: state.settings, store: store, onResult: onResult)
        } catch {
            show("Не удалось проверить серверы: \(error.localizedDescription)", error: true)
            // Not checked, so no time: an old one would pass for a new one.
            for id in pendingPings { state.setPing(nil, for: id) }
        }
        persist()
    }

    func pingAll() async {
        await ping(state.servers)
    }

    func ping(subscription id: UUID) async {
        guard !pinging else { return }
        pingingSubscription = id
        defer { pingingSubscription = nil }
        await ping(state.servers(in: id))
    }

    // MARK: rule-sets

    func updateRuleSets(force: Bool) async {
        let tags = RuleSets.needed(for: state.settings)
        let updater = RuleSetUpdater()
        guard !tags.isEmpty, !updatingRuleSets else { return }
        guard force || updater.isDue(tags, in: store.ruleSetDirectory) else { return }
        updatingRuleSets = true
        defer { updatingRuleSets = false }
        let failed = await updater.update(tags, into: store.ruleSetDirectory)
        applyConfig()
        if !failed.isEmpty {
            show("Не удалось скачать базы: \(failed.joined(separator: ", "))", error: true)
        } else if force {
            show("Базы маршрутизации обновлены")
        }
    }

    var ruleSetsReady: Bool {
        let needed = Set(RuleSets.needed(for: state.settings))
        return needed.isSubset(of: RuleSets.available(in: store.ruleSetDirectory))
    }

    // MARK: lifecycle

    private var activating = false

    /// The app came to the screen.
    func didBecomeActive() async {
        guard !activating else { return }
        activating = true
        defer { activating = false }
        await vpn.load()
        if vpn.isActive { await vpn.send(.wake) }
        await updateRuleSets(force: false)
        if state.settings.pingOnOpen { await pingAll() }
    }

    /// Background refresh: subscriptions and rule-sets only.
    func backgroundRefresh() async {
        await refreshDue(ping: false)
        await updateRuleSets(force: false)
    }
}
