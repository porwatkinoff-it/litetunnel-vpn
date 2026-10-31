import LiteKit
import PhotosUI
import SwiftUI

/// Add a subscription or servers: from the clipboard, typed in, or a QR code.
struct AddView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var photo: PhotosPickerItem?
    @State private var working = false
    @State private var scanning = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    PasteButton(payloadType: String.self) { strings in
                        Task { await add(strings.joined(separator: "\n")) }
                    }
                    .disabled(working)
                } header: {
                    Text("Из буфера обмена")
                } footer: {
                    Text("Ссылка на подписку (https://…) или на серверы: vless://, hy2://, trojan://, ss://, vmess://, tuic://. Можно несколько строк сразу.")
                }

                Section("Вставить вручную") {
                    TextField("vless://… или https://…", text: $text, axis: .vertical)
                        .lineLimit(3...8)
                        .font(.callout.monospaced())
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                    Button {
                        Task { await add(text) }
                    } label: {
                        Label("Добавить", systemImage: "plus.circle")
                    }
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || working)
                }

                Section("QR-код") {
                    #if os(iOS)
                    if QRScannerView.isAvailable {
                        Button {
                            scanning = true
                        } label: {
                            Label("Сканировать камерой", systemImage: "qrcode.viewfinder")
                        }
                    }
                    #endif
                    PhotosPicker(selection: $photo, matching: .images) {
                        Label("Из фото или скриншота", systemImage: "photo")
                    }
                }

                if working {
                    HStack {
                        ProgressView()
                        Text("Загружаю…").foregroundStyle(.secondary)
                    }
                }
            }
            .formStyle(.grouped)
            .textCase(nil)
            .navigationTitle("Добавить")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Отмена") { dismiss() }
                }
            }
            .onChange(of: photo) { item in
                guard let item else { return }
                Task { await read(item) }
            }
            #if os(iOS)
            .fullScreenCover(isPresented: $scanning) {
                QRScannerSheet { code in
                    scanning = false
                    Task { await add(code) }
                }
            }
            #endif
        }
        #if os(macOS)
        .frame(minWidth: 440, minHeight: 420)
        #endif
    }

    private func add(_ input: String) async {
        working = true
        defer { working = false }
        if await model.importText(input) { dismiss() }
    }

    private func read(_ item: PhotosPickerItem) async {
        defer { photo = nil }
        guard let data = try? await item.loadTransferable(type: Data.self) else {
            model.show("Не удалось открыть изображение", error: true)
            return
        }
        let codes = QRCode.decode(data)
        guard !codes.isEmpty else {
            model.show("На картинке нет QR-кода", error: true)
            return
        }
        await add(codes.joined(separator: "\n"))
    }
}
