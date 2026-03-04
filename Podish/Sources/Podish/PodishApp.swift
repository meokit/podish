import AppKit
import SwiftUI
import SwiftTerm

@MainActor
final class TerminalSession: ObservableObject {
    let view: LocalProcessTerminalView

    init() {
        view = LocalProcessTerminalView(frame: .zero)
        view.startProcess(executable: "/bin/zsh", args: ["-l"])
    }
}

struct TerminalContainer: NSViewRepresentable {
    @ObservedObject var session: TerminalSession

    func makeNSView(context: Context) -> LocalProcessTerminalView {
        session.view
    }

    func updateNSView(_ nsView: LocalProcessTerminalView, context: Context) {}
}

struct PodishRootView: View {
    @StateObject private var session = TerminalSession()

    var body: some View {
        TerminalContainer(session: session)
            .frame(minWidth: 900, minHeight: 560)
    }
}

@main
struct PodishApp: App {
    var body: some Scene {
        WindowGroup("Podish") {
            PodishRootView()
        }
    }
}
