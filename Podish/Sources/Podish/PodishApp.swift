import SwiftUI

#if os(macOS)
import AppKit

final class PodishApplicationDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        _ = NSApplication.shared.setActivationPolicy(.regular)
        NSApplication.shared.activate(ignoringOtherApps: true)
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
            PodishRootView()
                .frame(minWidth: 1200, minHeight: 760)
            #else
            PodishRootView()
            #endif
        }
    }
}
