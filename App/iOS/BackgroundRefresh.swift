import BackgroundTasks
import Foundation

/// Subscription refresh while the app is closed. iOS decides when it runs;
/// we only ask for the earliest moment a subscription becomes due.
enum BackgroundRefresh {
    static var identifier: String { (Bundle.main.bundleIdentifier ?? "litetunnel") + ".refresh" }

    static func schedule(at date: Date) {
        let request = BGAppRefreshTaskRequest(identifier: identifier)
        request.earliestBeginDate = date
        BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: identifier)
        try? BGTaskScheduler.shared.submit(request)
    }

    @MainActor static func run() async {
        await Services.model.backgroundRefresh()
        schedule(at: Services.model.nextRefreshDate)
    }
}
