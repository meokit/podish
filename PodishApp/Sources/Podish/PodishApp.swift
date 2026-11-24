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

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
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

    @StateObject private var desktopSession = PodishDesktopSession()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup("Podish Desktop") {
            DesktopRootView(session: desktopSession)
                .modifier(ScenePhaseObserver(scenePhase: scenePhase) { phase in
                    switch phase {
                    case .active:
                        desktopSession.setVisible(true)
                    case .background:
                        desktopSession.setVisible(false)
                    default:
                        break
                    }
                })
                #if os(macOS)
                .frame(minWidth: 640, minHeight: 480)
                .onAppear {
                    appDelegate.onBeforeTerminate = { completion in
                        desktopSession.stopForAppTermination(completion: completion)
                    }
                }
                #endif
        }
        #if os(macOS)
        .defaultSize(width: 1280, height: 800)
        .commands {
            CommandMenu("Desktop") {
                Button("Show Launcher") {
                    desktopSession.focusLauncher()
                }
                .keyboardShortcut("l", modifiers: [.command])

                Divider()

                Button("Restart Desktop Session") {
                    desktopSession.restart()
                }

                Button("Reset User Data (Clear SilkFS Overlay)") {
                    desktopSession.resetUserDataAndRestart()
                }
            }
        }
        #endif
    }
}

private struct ScenePhaseObserver: ViewModifier {
    let scenePhase: ScenePhase
    let onChange: (ScenePhase) -> Void

    func body(content: Content) -> some View {
        if #available(iOS 17.0, macOS 14.0, *) {
            content.onChange(of: scenePhase) { _, newPhase in
                onChange(newPhase)
            }
        } else {
            content.onChange(of: scenePhase, perform: onChange)
        }
    }
}
