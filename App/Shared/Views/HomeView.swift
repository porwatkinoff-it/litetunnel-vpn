import LiteKit
import SwiftUI
#if os(macOS)
import AppKit
#endif

/// The connect button on top, then servers grouped by subscription.
struct HomeView: View {
    @EnvironmentObject private var model: AppModel
    @State private var path: [UUID] = []
    @State private var showAdd = false
    @State private var showSettings = false
    @State private var editing: Subscription?
    /// Collapsed subscription IDs, comma-separated. A view preference, so it
    /// stays out of AppState and the tunnel's copy of it.
    @AppStorage("collapsedSubscriptions") private var collapsedIDs = ""

    var body: some View {
        NavigationStack(path: $path) {
            List {
                Section {
                    ConnectCard()
                }

                if model.state.servers.isEmpty && model.state.subscriptions.isEmpty {
                    Section {
                        EmptyServers { showAdd = true }
                    }
                }

                ForEach(model.state.subscriptions) { sub in
                    let collapsed = collapsedIDs.contains(sub.id.uuidString)
                    Section {
                        if !collapsed {
                            if let announce = sub.announce, !announce.isEmpty {
                                Label(announce, systemImage: "megaphone")
                                    .font(.footnote)
                                    .foregroundStyle(.secondary)
                            }
                            ForEach(model.state.sorted(model.state.servers(in: sub.id))) { server in
                                row(server)
                            }
                        }
                    } header: {
                        SubscriptionHeader(subscription: sub, collapsed: collapsed,
                                           onToggle: { toggleCollapsed(sub.id) },
                                           onEdit: { editing = sub })
                    }
                }

                let manual = model.state.servers(in: nil)
                if !manual.isEmpty {
                    Section("Мои серверы") {
                        ForEach(model.state.sorted(manual)) { server in
                            row(server)
                                .swipeActions {
                                    Button(role: .destructive) {
                                        model.removeServer(server.id)
                                    } label: {
                                        Label("Удалить", systemImage: "trash")
                                    }
                                }
                        }
                        .onMove { from, to in
                            model.moveServers(in: nil, from: from, to: to)
                        }
                        .moveDisabled(model.settings.sortMode != .none)
                    }
                }
            }
            .textCase(nil)
            .refreshable { await model.refreshAll() }
            .navigationTitle("LiteTunnel")
            .navigationDestination(for: UUID.self) { id in
                ServerDetailView(serverID: id)
            }
            .toolbar { toolbar }
            .sheet(isPresented: $showAdd) {
                AddView()
            }
            .sheet(item: $editing) { sub in
                SubscriptionView(subscription: sub)
            }
            #if os(iOS)
            .sheet(isPresented: $showSettings) {
                NavigationStack {
                    SettingsView()
                        .toolbar {
                            ToolbarItem(placement: .confirmationAction) {
                                Button("Готово") { showSettings = false }
                            }
                        }
                }
            }
            #endif
        }
    }

    private func toggleCollapsed(_ id: UUID) {
        var ids = Set(collapsedIDs.split(separator: ",").map(String.init))
        // Drop subscriptions that no longer exist while at it.
        ids.formIntersection(model.state.subscriptions.map(\.id.uuidString))
        if ids.remove(id.uuidString) == nil { ids.insert(id.uuidString) }
        withAnimation(.easeInOut(duration: 0.2)) {
            collapsedIDs = ids.sorted().joined(separator: ",")
        }
    }

    private func row(_ server: Server) -> some View {
        ServerRow(server: server, selected: server.id == model.state.selectedServerID,
                  pingDisplay: model.settings.pingDisplay) {
            path.append(server.id)
        }
        .contentShape(Rectangle())
        .onTapGesture { model.select(server.id) }
        .contextMenu {
            Button {
                path.append(server.id)
            } label: {
                Label("Подробнее", systemImage: "info.circle")
            }
            Button {
                Task { await model.ping([server]) }
            } label: {
                Label("Проверить пинг", systemImage: "bolt.horizontal")
            }
            if let link = server.shareLink {
                Button {
                    Clipboard.copy(link)
                    model.show("Ссылка скопирована")
                } label: {
                    Label("Копировать ссылку", systemImage: "doc.on.doc")
                }
            }
            if server.subscriptionID == nil {
                Button(role: .destructive) {
                    model.removeServer(server.id)
                } label: {
                    Label("Удалить", systemImage: "trash")
                }
            }
        }
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItem(placement: .primaryAction) {
            Button {
                showAdd = true
            } label: {
                Label("Добавить", systemImage: "plus")
            }
            .keyboardShortcut("n")
        }
        ToolbarItem {
            Button {
                Task { await model.pingAll() }
            } label: {
                Label("Пинг всех", systemImage: model.pinging ? "bolt.horizontal.fill" : "bolt.horizontal")
            }
            .disabled(model.pinging || model.state.servers.isEmpty)
        }
        #if os(iOS)
        ToolbarItem(placement: .navigationBarLeading) {
            Button {
                showSettings = true
            } label: {
                Label("Настройки", systemImage: "gearshape")
            }
        }
        #else
        ToolbarItem {
            Button {
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            } label: {
                Label("Настройки", systemImage: "gearshape")
            }
        }
        #endif
    }
}

struct ServerRow: View {
    let server: Server
    let selected: Bool
    let pingDisplay: PingDisplay
    let onInfo: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                .imageScale(.large)
            VStack(alignment: .leading, spacing: 2) {
                Text(server.name)
                    .lineLimit(1)
                Text([server.proto.title, server.detail].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if let ping = server.lastPing {
                PingBadge(ping: ping, display: pingDisplay)
            }
            Button(action: onInfo) {
                Image(systemName: "info.circle")
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("Подробнее о сервере")
        }
        .padding(.vertical, 2)
    }
}

/// Name with a fold arrow (tap to collapse), then ping, refresh and the menu.
struct SubscriptionHeader: View {
    @EnvironmentObject private var model: AppModel
    let subscription: Subscription
    let collapsed: Bool
    let onToggle: () -> Void
    let onEdit: () -> Void
    @State private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 14) {
                Button(action: onToggle) {
                    HStack(spacing: 6) {
                        Text(subscription.name)
                            .font(.headline)
                            // Color, not .primary: in a section header .primary
                            // resolves to the header's grey.
                            .foregroundStyle(Color.primary)
                            .lineLimit(1)
                        Image(systemName: "chevron.down")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                            .rotationEffect(.degrees(collapsed ? -90 : 0))
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(subscription.name)
                .accessibilityValue(collapsed ? "свёрнута" : "развёрнута")
                .accessibilityHint(collapsed ? "Показать серверы" : "Скрыть серверы")

                iconButton("Пинг серверов", systemImage: "bolt.horizontal",
                           busy: model.pingingSubscription == subscription.id) {
                    Task { await model.ping(subscription: subscription.id) }
                }
                .disabled(model.pinging || model.state.servers(in: subscription.id).isEmpty)

                iconButton("Обновить подписку", systemImage: "arrow.clockwise",
                           busy: model.refreshing.contains(subscription.id)) {
                    Task { await model.refresh(subscription.id) }
                }

                Menu {
                    Button(action: onEdit) {
                        Label("Настроить", systemImage: "slider.horizontal.3")
                    }
                    if let support = subscription.supportURL {
                        Link(destination: support) {
                            Label("Поддержка", systemImage: "questionmark.bubble")
                        }
                    }
                    if let page = subscription.webPageURL {
                        Link(destination: page) {
                            Label("Сайт провайдера", systemImage: "safari")
                        }
                    }
                    Divider()
                    Button(role: .destructive) {
                        confirmDelete = true
                    } label: {
                        Label("Удалить подписку", systemImage: "trash")
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .imageScale(.large)
                }
                #if os(macOS)
                .menuStyle(.borderlessButton)
                #endif
                .fixedSize()
                .accessibilityLabel("Ещё")
            }
            if let usage = subscription.usage {
                UsageLine(usage: usage)
            }
            if let error = subscription.lastError {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .lineLimit(2)
            } else if let updated = subscription.lastUpdated {
                // Folded, the count says what is hidden.
                Text(["Обновлена \(Format.relative(updated))",
                      collapsed ? Format.servers(model.state.servers(in: subscription.id).count) : nil]
                    .compactMap { $0 }.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
        .confirmationDialog("Удалить подписку «\(subscription.name)» и её серверы?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Удалить", role: .destructive) { model.removeSubscription(subscription.id) }
        }
    }

    /// A header icon that turns into a spinner while its job runs.
    private func iconButton(_ title: String, systemImage: String, busy: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            ZStack {
                if busy {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: systemImage)
                        .imageScale(.large)
                }
            }
            .frame(width: 24, height: 24)
        }
        .buttonStyle(.borderless)
        .disabled(busy)
        .accessibilityLabel(title)
    }
}

struct UsageLine: View {
    let usage: Usage

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            if let fraction = usage.fraction {
                ProgressView(value: min(1, fraction))
                    .tint(fraction > 0.9 ? .red : .accentColor)
            }
            HStack {
                Text(usage.total > 0
                     ? "\(Format.size(usage.used)) из \(Format.size(usage.total))"
                     : "\(Format.size(usage.used)) · без лимита")
                Spacer()
                if let expire = usage.expire {
                    Text(Format.expiry(expire))
                        .foregroundStyle(expire < Date() ? .red : .secondary)
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }
}

struct EmptyServers: View {
    let onAdd: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "server.rack")
                .font(.largeTitle)
                .foregroundStyle(.secondary)
            Text("Серверов пока нет")
                .font(.headline)
            Text("Добавьте ссылку на подписку или сервер: vless://, hy2://, trojan://, ss:// и другие.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button("Добавить", action: onAdd)
                .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 20)
    }
}
