import SwiftUI

struct TerminalWorkspaceView: View {
    @ObservedObject var store: PodishUiStore
    @StateObject private var session = PodishTerminalSession()

    var body: some View {
        ZStack(alignment: .topLeading) {
            TerminalViewHost(terminalView: session.terminalView)
                .background(Color.black)
                .onAppear {
                    session.startIfNeeded()
                }
                .onDisappear {
                    session.stop()
                }

            if let startupError = session.startupError {
                Text(startupError)
                    .font(.caption)
                    .foregroundStyle(.white)
                    .padding(8)
                    .background(.red.opacity(0.75), in: RoundedRectangle(cornerRadius: 8))
                    .padding(12)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
