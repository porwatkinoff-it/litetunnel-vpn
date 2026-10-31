import Foundation

/// Subscription refresh on the Mac. The system batches the work with other
/// apps' maintenance, so the Mac wakes for it rarely.
enum BackgroundRefresh {
    private static var scheduler: NSBackgroundActivityScheduler?

    static func start(_ model: AppModel) {
        guard scheduler == nil else { return }
        let activity = NSBackgroundActivityScheduler(identifier: (Bundle.main.bundleIdentifier ?? "litetunnel") + ".refresh")
        activity.repeats = true
        activity.interval = 3 * 60 * 60
        activity.tolerance = 60 * 60
        activity.qualityOfService = .utility
        activity.schedule { completion in
            Task { @MainActor in
                await model.backgroundRefresh()
                completion(.finished)
            }
        }
        scheduler = activity
    }
}
