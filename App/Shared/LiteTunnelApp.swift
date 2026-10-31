import LiteKit
import SwiftUI

/// One model for the whole app, reachable from background work too.
@MainActor
enum Services {
    static let vpn = VPNController()
    static let model = AppModel(vpn: vpn)
}

@main
struct LiteTunnelApp: App {
    @StateObject private var vpn = Services.vpn
    @StateObject private var model = Services.model

    var body: some Scene {
        #if os(iOS)
        WindowGroup {
            RootView()
                .environmentObject(model)
                .environmentObject(vpn)
        }
        .backgroundTask(.appRefresh(BackgroundRefresh.identifier)) {
            await BackgroundRefresh.run()
        }
        #else
        Window("LiteTunnel", id: MainWindow.id) {
            RootView()
                .environmentObject(model)
                .environmentObject(vpn)
                .frame(minWidth: 420, maxWidth: 640, minHeight: 640)
                .onAppear { BackgroundRefresh.start(model) }
        }
        .defaultSize(width: 440, height: 700)
        // The list is one column, 640 wide at most, so the window is too.
        .windowResizability(.contentSize)

        MenuBarExtra {
            MenuBarView()
                .environmentObject(model)
                .environmentObject(vpn)
        } label: {
            Image(systemName: vpn.isActive ? "lock.shield.fill" : "lock.shield")
        }
        .menuBarExtraStyle(.window)

        Settings {
            NavigationStack { SettingsView() }
                .environmentObject(model)
                .environmentObject(vpn)
                .frame(width: 480, height: 600)
        }
        #endif
    }
}

/// The main screen plus what every platform needs around it: links, the
/// foreground hook and the banner.
struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        HomeView()
            .overlay(alignment: .top) { BannerView() }
            .onOpenURL { url in
                Task { await model.open(url) }
            }
            .task { await model.didBecomeActive() }
            .onChange(of: scenePhase) { phase in
                switch phase {
                case .active:
                    Task { await model.didBecomeActive() }
                case .background:
                    #if os(iOS)
                    BackgroundRefresh.schedule(at: model.nextRefreshDate)
                    #endif
                default:
                    break
                }
            }
    }
}

struct BannerView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        if let banner = model.banner {
            Label(banner.text, systemImage: banner.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .font(.callout)
                .foregroundStyle(banner.isError ? Color.white : Color.primary)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(banner.isError ? AnyShapeStyle(Color.red.gradient) : AnyShapeStyle(.regularMaterial),
                            in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .shadow(radius: 6, y: 2)
                .padding(.horizontal)
                .padding(.top, 8)
                .transition(.move(edge: .top).combined(with: .opacity))
                .onTapGesture { model.banner = nil }
                .task(id: banner.id) {
                    try? await Task.sleep(nanoseconds: 3_500_000_000)
                    if model.banner?.id == banner.id {
                        withAnimation { model.banner = nil }
                    }
                }
        }
    }
}

/// Asks the tunnel for speed once a second, only while the view is on
/// screen, the app is in front and the tunnel is up.
struct TrafficPolling: ViewModifier {
    @EnvironmentObject private var vpn: VPNController
    @Environment(\.scenePhase) private var scenePhase

    func body(content: Content) -> some View {
        content.task(id: active) {
            guard active else { return }
            while !Task.isCancelled {
                await vpn.pollTraffic()
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }
    }

    private var active: Bool {
        vpn.status == .connected && scenePhase == .active
    }
}

extension View {
    func trafficPolling() -> some View { modifier(TrafficPolling()) }
}
