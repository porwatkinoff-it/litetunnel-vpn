import LiteKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        let settings = model.settingsBinding
        Form {
            Section {
                Picker("Режим", selection: settings.routing) {
                    ForEach(RoutingMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                Toggle("Блокировать рекламу", isOn: settings.blockAds)
                NavigationLink("Свои правила для доменов") {
                    DomainRulesView()
                }
                if !RuleSets.needed(for: model.settings).isEmpty {
                    HStack {
                        Button("Обновить базы") {
                            Task { await model.updateRuleSets(force: true) }
                        }
                        .disabled(model.updatingRuleSets)
                        Spacer()
                        if model.updatingRuleSets {
                            ProgressView().controlSize(.small)
                        } else if !model.ruleSetsReady {
                            Text("не скачаны").foregroundStyle(.orange)
                        }
                    }
                }
            } header: {
                Text("Маршрутизация")
            } footer: {
                Text("«Россия напрямую»: российские сайты и адреса идут мимо VPN. Базы доменов и адресов скачиваются раз в неделю.")
            }

            Section {
                DNSField(title: "Через VPN", server: settings.remoteDNS)
                DNSField(title: "Напрямую", server: settings.directDNS)
            } header: {
                Text("DNS")
            } footer: {
                Text("UDP — адрес вроде 77.88.8.8; DoT и DoH — адрес или имя сервера, например 1.1.1.1 или dns.google.")
            }

            Section {
                if VPNController.usesSystemProxy {
                    // A proxy has nothing to keep traffic from going around it.
                    Toggle("Локальная сеть мимо прокси", isOn: settings.excludeLocalNetworks)
                } else {
                    Toggle("Kill switch", isOn: settings.killSwitch)
                    Toggle("Локальная сеть мимо VPN", isOn: settings.excludeLocalNetworks)
                }
                Toggle("IPv6", isOn: settings.ipv6)
                Toggle("Фрагментация TLS", isOn: settings.tlsFragment)
            } header: {
                Text("Подключение")
            } footer: {
                if VPNController.usesSystemProxy {
                    Text("На Mac без подписи Apple трафик идёт через системный прокси, а не VPN: программы, которые его не используют, ходят напрямую. Фрагментация TLS помогает, когда провайдер режет соединения по имени сайта.")
                } else {
                    Text("Kill switch не выпускает трафик мимо туннеля, даже пока он переподключается. Фрагментация TLS помогает, когда провайдер режет соединения по имени сайта.")
                }
            }

            Section {
                Toggle("Умный сон", isOn: settings.smartSleep)
            } header: {
                Text("Энергосбережение")
            } footer: {
                Text("Пока устройство спит, ядро закрывает простаивающие соединения и останавливает таймеры. Просыпается, как только включается экран.")
            }

            Section("Список серверов") {
                Picker("Сортировка", selection: settings.sortMode) {
                    ForEach(SortMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
            }

            Section {
                Picker("Способ", selection: settings.pingMode) {
                    ForEach(PingMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                PingURLField(url: settings.pingURL)
                Picker("Вид", selection: settings.pingDisplay) {
                    ForEach(PingDisplay.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                Toggle("Пинг при открытии", isOn: settings.pingOnOpen)
                Toggle("Пинг после обновления подписки", isOn: settings.pingOnUpdate)
            } header: {
                Text("Пинг")
            } footer: {
                Text(model.settings.pingMode.footer)
            }

            Section {
                Toggle("Определять сайт (sniffing)", isOn: settings.sniffing)
                Picker("Сетевой стек", selection: settings.tunStack) {
                    ForEach(TunStack.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                MTUField(mtu: settings.mtu)
                Picker("Журнал", selection: settings.logLevel) {
                    ForEach(LogLevel.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
                NavigationLink("Открыть журнал") {
                    LogsView()
                }
            } header: {
                Text("Дополнительно")
            }

            Section {
                LabeledContent("Версия", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—")
                Link("Ядро sing-box (GPLv3)", destination: URL(string: "https://github.com/SagerNet/sing-box")!)
                if !model.hasAppGroup {
                    Text("Сборка без App Group: туннель не запустится. Укажите команду разработчика в Config/Signing.xcconfig.")
                        .foregroundStyle(.orange)
                }
            } header: {
                Text("О приложении")
            }
        }
        .formStyle(.grouped)
        .textCase(nil)
        .navigationTitle("Настройки")
    }
}

/// Edits a DNS server; saves when the field is submitted or left.
struct DNSField: View {
    let title: String
    @Binding var server: DNSServer
    @State private var address = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker(title, selection: $server.kind) {
                ForEach(DNSServer.Kind.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            TextField("Адрес", text: $address)
                .font(.callout.monospaced())
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                .keyboardType(.URL)
                #endif
                .focused($focused)
                .onSubmit(commit)
                .onChange(of: focused) { isFocused in
                    if !isFocused { commit() }
                }
        }
        .onAppear { address = server.address }
        .onChange(of: server.address) { address = $0 }
    }

    private func commit() {
        let trimmed = address.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed != server.address else {
            address = server.address
            return
        }
        server.address = trimmed
    }
}

struct MTUField: View {
    @Binding var mtu: Int
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        LabeledContent("MTU") {
            TextField("", text: $text, prompt: Text("авто"))
                .labelsHidden()
                .multilineTextAlignment(.trailing)
                .monospacedDigit()
                #if os(iOS)
                .keyboardType(.numberPad)
                #endif
                .focused($focused)
                .onSubmit(commit)
                .onChange(of: focused) { isFocused in
                    if !isFocused { commit() }
                }
        }
        .onAppear { text = mtu > 0 ? String(mtu) : "" }
    }

    private func commit() {
        let value = Int(text.trimmingCharacters(in: .whitespaces)) ?? 0
        // 0 lets the core pick; anything else must fit a real link.
        let clamped = value == 0 ? 0 : min(max(value, 1280), 9000)
        text = clamped > 0 ? String(clamped) : ""
        if clamped != mtu { mtu = clamped }
    }
}

/// The address the proxy ping modes request. Saved when editing ends; an
/// empty field goes back to the default.
struct PingURLField: View {
    @Binding var url: String
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        LabeledContent("Адрес проверки") {
            // On macOS a form shows a field's title as a second label.
            TextField("", text: $text, prompt: Text(AppSettings.defaultPingURL))
                .labelsHidden()
                .multilineTextAlignment(.trailing)
                .autocorrectionDisabled()
                #if os(iOS)
                .keyboardType(.URL)
                .textInputAutocapitalization(.never)
                #endif
                .focused($focused)
                .onSubmit(commit)
                .onChange(of: focused) { isFocused in
                    if !isFocused { commit() }
                }
        }
        .onAppear { text = url }
    }

    private func commit() {
        let value = text.trimmingCharacters(in: .whitespaces)
        let saved = value.isEmpty ? AppSettings.defaultPingURL : ProxyProbe.testURL(value).absoluteString
        text = saved
        if saved != url { url = saved }
    }
}

/// Domains that always go direct, always through VPN, or are blocked.
struct DomainRulesView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        let settings = model.settingsBinding
        Form {
            DomainListSection(title: "Через VPN", hint: "Например, youtube.com", domains: settings.proxyDomains)
            DomainListSection(title: "Напрямую", hint: "Например, bank.ru", domains: settings.directDomains)
            DomainListSection(title: "Блокировать", hint: "Например, ads.example.com", domains: settings.blockDomains)
            Section {
                EmptyView()
            } footer: {
                Text("Правило действует на домен и все его поддомены. Блокировка важнее остальных, «через VPN» важнее «напрямую».")
            }
        }
        .formStyle(.grouped)
        .textCase(nil)
        .navigationTitle("Свои правила")
    }
}

struct DomainListSection: View {
    let title: String
    let hint: String
    @Binding var domains: [String]
    @State private var input = ""

    var body: some View {
        Section(title) {
            ForEach(domains, id: \.self) { domain in
                Text(domain)
                    .font(.callout.monospaced())
                    .contextMenu {
                        Button("Удалить", role: .destructive) { domains.removeAll { $0 == domain } }
                    }
            }
            .onDelete { domains.remove(atOffsets: $0) }
            HStack {
                TextField(hint, text: $input)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .onSubmit(add)
                Button("Добавить", action: add)
                    .disabled(Self.normalize(input) == nil)
            }
        }
    }

    private func add() {
        guard let domain = Self.normalize(input) else { return }
        if !domains.contains(domain) { domains.append(domain) }
        input = ""
    }

    /// "https://www.Example.com/path" → "example.com".
    static func normalize(_ raw: String) -> String? {
        var text = raw.trimmingCharacters(in: .whitespaces).lowercased()
        if let url = URL(string: text), let host = url.host { text = host }
        if text.hasPrefix("www.") { text.removeFirst(4) }
        if text.hasPrefix(".") { text.removeFirst() }
        guard text.contains("."), !text.contains(" "), !text.contains("/") else { return nil }
        return text
    }
}
