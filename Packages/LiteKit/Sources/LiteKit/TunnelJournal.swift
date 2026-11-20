import Foundation

/// A short record of when the tunnel started and why it stopped, kept next to
/// the core's files so «Журнал» can show it after the VPN is off.
///
/// The tunnel writes a marker when it starts and removes it when the system
/// stops it. A marker left behind means the process ended without a stop:
/// iOS unloaded it, almost always for using too much memory.
/// Written only on start, stop and errors: nothing here runs on a timer.
public struct TunnelJournal {
    public static let maxLines = 40

    let file: URL
    let marker: URL

    public init(folder: URL) {
        file = folder.appendingPathComponent("journal.log")
        marker = folder.appendingPathComponent("tunnel.running")
    }

    public init(store: SharedStore) {
        self.init(folder: store.workingDirectory)
    }

    /// The tunnel started. Returns true when the run before it was cut off.
    @discardableResult
    public func started(at date: Date = Date()) -> Bool {
        let cutOff = cutOffLine()
        if let cutOff { append(cutOff) }
        append(Self.line("VPN включён", at: date))
        try? Data(Self.stamp(date).utf8).write(to: marker, options: .atomic)
        return cutOff != nil
    }

    /// The system stopped the tunnel. `reason` is `NEProviderStopReason`.
    public func stopped(reason: Int, at date: Date = Date()) {
        try? FileManager.default.removeItem(at: marker)
        append(Self.line("VPN выключен: \(Self.describe(reason))", at: date))
    }

    /// The tunnel did not start.
    public func failed(_ message: String, at date: Date = Date()) {
        try? FileManager.default.removeItem(at: marker)
        append(Self.line("Не удалось включить: \(message)", at: date))
    }

    public func note(_ text: String, at date: Date = Date()) {
        append(Self.line(text, at: date))
    }

    /// The journal, oldest first. With the VPN off, a marker still there
    /// means the last run was cut off; that line is shown, not written.
    public func lines(running: Bool) -> [String] {
        var lines = read()
        if !running, let cutOff = cutOffLine() { lines.append(cutOff) }
        return lines
    }

    public static func describe(_ reason: Int) -> String {
        switch reason {
        case 1: return "вручную (приложение, виджет, пункт управления или Настройки)"
        case 2: return "ошибка туннеля"
        case 3: return "нет сети"
        case 4: return "сеть сменилась, и система не смогла продолжить"
        case 5: return "профиль VPN отключён"
        case 6: return "проверка отменена"
        case 7: return "ошибка настройки VPN"
        case 8: return "простой"
        case 9: return "профиль VPN выключен в Настройках"
        case 10: return "профиль VPN удалён"
        case 11: return "включён другой VPN"
        case 12: return "выход из системы"
        case 13: return "смена пользователя"
        case 14: return "не удалось подключиться"
        case 15: return "сон устройства"
        case 16: return "обновление приложения"
        case 17: return "внутренняя ошибка системы"
        default: return "причина \(reason)"
        }
    }

    private func cutOffLine() -> String? {
        guard let data = try? Data(contentsOf: marker) else { return nil }
        let since = String(decoding: data, as: UTF8.self)
        return "VPN оборвался: система выгрузила туннель, включённый \(since), без остановки (обычно из-за нехватки памяти)"
    }

    private func read() -> [String] {
        guard let data = try? Data(contentsOf: file) else { return [] }
        return String(decoding: data, as: UTF8.self)
            .split(separator: "\n", omittingEmptySubsequences: true)
            .map(String.init)
    }

    private func append(_ line: String) {
        let lines = (read() + [line]).suffix(Self.maxLines)
        try? Data((lines.joined(separator: "\n") + "\n").utf8).write(to: file, options: .atomic)
    }

    private static func line(_ text: String, at date: Date) -> String {
        "\(stamp(date)) \(text)"
    }

    private static func stamp(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "ru_RU")
        formatter.dateFormat = "dd.MM HH:mm:ss"
        return formatter.string(from: date)
    }
}
