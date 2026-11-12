import LiteKit
import SwiftUI

/// One subscription's settings and what its last update reported.
struct SubscriptionView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: Subscription
    @State private var urlText: String
    @State private var userAgent: String
    @State private var confirmDelete = false

    init(subscription: Subscription) {
        _draft = State(initialValue: subscription)
        _urlText = State(initialValue: subscription.url.absoluteString)
        _userAgent = State(initialValue: subscription.userAgent ?? "")
    }

    private var current: Subscription {
        model.state.subscriptions.first { $0.id == draft.id } ?? draft
    }

    private var validURL: URL? {
        let trimmed = urlText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", url.host != nil
        else { return nil }
        return url
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Название", text: $draft.name)
                    TextField("https://…", text: $urlText, axis: .vertical)
                        .font(.callout.monospaced())
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        #endif
                    if validURL == nil {
                        Text("Нужен адрес http:// или https://")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                } header: {
                    Text("Подписка")
                }

                Section {
                    Stepper(value: $draft.updateIntervalHours, in: 1...168) {
                        LabeledContent("Обновлять в фоне каждые", value: "\(draft.updateIntervalHours) ч")
                    }
                    TextField("User-Agent", text: $userAgent, prompt: Text(SubscriptionClient.defaultUserAgent))
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                } header: {
                    Text("Обновление")
                } footer: {
                    Text("Обновляется в фоне, когда система разрешит, и по кнопке обновления. При открытии приложения не обновляется. Некоторые провайдеры отдают разные списки разным клиентам: тогда поможет другой User-Agent.")
                }

                Section("Состояние") {
                    LabeledContent("Серверов", value: "\(model.state.serverCount(in: draft.id))")
                    if current.skippedCount > 0 {
                        LabeledContent("Пропущено строк", value: "\(current.skippedCount)")
                        if let reasons = current.skippedReasons, !reasons.isEmpty {
                            Text(reasons.joined(separator: "\n"))
                                .font(.footnote)
                                .foregroundStyle(.secondary)
                        }
                    }
                    if let updated = current.lastUpdated {
                        LabeledContent("Обновлена", value: Format.relative(updated))
                    }
                    if let error = current.lastError {
                        Text(error).foregroundStyle(.red)
                    }
                    if let usage = current.usage {
                        UsageLine(usage: usage)
                    }
                    if let support = current.supportURL {
                        Link("Поддержка провайдера", destination: support)
                    }
                    if let page = current.webPageURL {
                        Link("Личный кабинет", destination: page)
                    }
                    Button {
                        save()
                        Task { await model.refresh(draft.id) }
                    } label: {
                        HStack {
                            Label("Обновить сейчас", systemImage: "arrow.clockwise")
                            if model.refreshing.contains(draft.id) {
                                Spacer()
                                ProgressView().controlSize(.small)
                            }
                        }
                    }
                    .disabled(validURL == nil || model.refreshing.contains(draft.id))
                }

                Section {
                    Button(role: .destructive) {
                        confirmDelete = true
                    } label: {
                        Label("Удалить подписку", systemImage: "trash")
                    }
                }
            }
            .formStyle(.grouped)
            .textCase(nil)
            .navigationTitle(draft.name)
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Готово") {
                        save()
                        dismiss()
                    }
                    .disabled(validURL == nil)
                }
            }
            .confirmationDialog("Удалить подписку и её серверы?", isPresented: $confirmDelete, titleVisibility: .visible) {
                Button("Удалить", role: .destructive) {
                    model.removeSubscription(draft.id)
                    dismiss()
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 460, minHeight: 520)
        #endif
    }

    private func save() {
        guard let url = validURL else { return }
        // Keep what a refresh may have written meanwhile; take only the edits.
        var updated = current
        updated.name = draft.name.trimmingCharacters(in: .whitespaces).isEmpty ? current.name : draft.name
        updated.url = url
        updated.updateIntervalHours = draft.updateIntervalHours
        let agent = userAgent.trimmingCharacters(in: .whitespaces)
        updated.userAgent = agent.isEmpty ? nil : agent
        model.updateSubscription(updated)
    }
}
