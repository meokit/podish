import SwiftUI

#if os(macOS)
import AppKit

final class PodishApplicationDelegate: NSObject, NSApplicationDelegate {
    var onBeforeTerminate: ((@escaping () -> Void) -> Void)?
    private var terminationInProgress = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        _ = NSApplication.shared.setActivationPolicy(.regular)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !terminationInProgress else { return .terminateLater }
        guard let onBeforeTerminate else { return .terminateNow }

        terminationInProgress = true
        onBeforeTerminate { [weak self] in
            Task { @MainActor in
                NSApplication.shared.reply(toApplicationShouldTerminate: true)
                self?.terminationInProgress = false
            }
        }
        return .terminateLater
    }
}
#endif

@main
struct PodishApp: App {
    #if os(macOS)
    @NSApplicationDelegateAdaptor(PodishApplicationDelegate.self) private var appDelegate
    #endif

    var body: some Scene {
        WindowGroup("Podish") {
            #if os(macOS)
            PodishRootView(onSessionReady: { session in
                appDelegate.onBeforeTerminate = { completion in
                    session.stopForAppTermination(completion: completion)
                }
            })
                .frame(minWidth: 1200, minHeight: 760)
            #else
            PodishRootView()
            #endif
        }
    }
}
