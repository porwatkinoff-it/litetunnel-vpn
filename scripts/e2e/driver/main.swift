import Foundation

// Runs the app's own subscription path: fetch -> parse -> apply -> build config.
// usage: driver <subscription-url> <out-dir>
let out = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let sub = Subscription(name: "e2e", url: URL(string: CommandLine.arguments[1])!)
var state = AppState()
state.subscriptions.append(sub)
let result = try await SubscriptionClient().fetch(sub)
state.apply(result, to: sub.id)
let s = state.subscriptions[0]
let f = ISO8601DateFormatter()
print("title=\(s.name) interval=\(s.updateIntervalHours)h skipped=\(s.skippedCount) " +
      "usage=\(s.usage.map { "up \($0.upload) down \($0.download) total \($0.total) expire \($0.expire.map(f.string) ?? "-")" } ?? "nil")")
for skipped in result.skipped { print("skipped: \(skipped.reason)") }
for (i, server) in state.servers.enumerated() {
    let config = ConfigBuilder.build(server: server, settings: AppSettings(), ruleSetDirectory: nil)
    try config.encoded(pretty: true).write(to: out.appendingPathComponent("client-\(i).json"))
    print("client-\(i)\t\(server.name)\t\(server.proto.rawValue)")
}
