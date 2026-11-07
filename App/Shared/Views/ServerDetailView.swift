import LiteKit
import SwiftUI

struct ServerDetailView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let serverID: UUID

    var body: some View {
        if let server = model.state.servers.first(where: { $0.id == serverID }) {
            content(server)
        } else {
            Text("Сервер удалён")
                .foregroundStyle(.secondary)
        }
    }

    private func content(_ server: Server) -> some View {
        Form {
            if let link = server.shareLink, let qr = QRCode.image(for: link) {
                Section {
                    Image(decorative: qr, scale: 1)
                        .interpolation(.none)
                        .resizable()
                        .scaledToFit()
                        .frame(maxWidth: 260)
                        .padding(8)
                        .background(Color.white, in: RoundedRectangle(cornerRadius: 12))
                        .frame(maxWidth: .infinity)
                } footer: {
                    Text("Отсканируйте на другом устройстве, чтобы добавить этот сервер.")
                }
            }

            Section {
                if server.members.isEmpty {
                    LabeledContent("Протокол", value: server.proto.title)
                    LabeledContent("Адрес", value: "\(server.host):\(server.port)")
                    if !server.detail.isEmpty {
                        LabeledContent("Транспорт", value: server.detail)
                    }
                }
                LabeledContent("Пинг") {
                    if model.pingingIDs.contains(server.id) {
                        PingSpinner()
                    } else if let ping = server.lastPing {
                        Text(ping.label).foregroundStyle(ping.color)
                    } else {
                        Text("—").foregroundStyle(.secondary)
                    }
                }
                if let subID = server.subscriptionID,
                   let sub = model.state.subscriptions.first(where: { $0.id == subID }) {
                    LabeledContent("Подписка", value: sub.name)
                }
            }

            let members = server.members
            if !members.isEmpty {
                Section {
                    ForEach(Array(members.enumerated()), id: \.offset) { _, member in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(member.name)
                                .lineLimit(1)
                            Text(member.subtitle)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                    }
                } header: {
                    Text("Серверы: \(members.count)")
                } footer: {
                    Text("Ядро само выбирает самый быстрый и переходит на другой, если этот перестал отвечать. Проверка раз в 10 минут.")
                }
            }

            Section {
                if model.state.selectedServerID != server.id {
                    Button {
                        model.select(server.id)
                    } label: {
                        Label("Выбрать этот сервер", systemImage: "checkmark.circle")
                    }
                }
                Button {
                    Task { await model.ping([server]) }
                } label: {
                    Label("Проверить пинг", systemImage: "bolt.horizontal")
                }
                .disabled(model.pinging)
                if let link = server.shareLink {
                    Button {
                        Clipboard.copy(link)
                        model.show("Ссылка скопирована")
                    } label: {
                        Label("Копировать ссылку", systemImage: "doc.on.doc")
                    }
                    ShareLink(item: link) {
                        Label("Поделиться", systemImage: "square.and.arrow.up")
                    }
                }
            }

            Section {
                DisclosureGroup("Настройки для ядра (JSON)") {
                    Text(server.outbound.jsonString(pretty: true))
                        .font(.caption.monospaced())
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            } footer: {
                Text("Здесь есть ключи доступа к серверу. Не публикуйте их.")
            }

            if server.subscriptionID == nil {
                Section {
                    Button(role: .destructive) {
                        model.removeServer(server.id)
                        dismiss()
                    } label: {
                        Label("Удалить сервер", systemImage: "trash")
                    }
                }
            }
        }
        .formStyle(.grouped)
        .textCase(nil)
        .navigationTitle(server.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }
}
