import SwiftUI

struct TerminalWorkspaceView: View {
    @ObservedObject var session: PodishTerminalSession

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder
    private var content: some View {
        #if os(iOS)
        GeometryReader { proxy in
            terminalSurface
                // Keep SwiftTerm's bottom toolbar above the home-indicator area.
                .padding(.bottom, max(8, proxy.safeAreaInsets.bottom))
        }
        #else
        terminalSurface
        #endif
    }

    private var terminalSurface: some View {
        ZStack(alignment: .topLeading) {
            TerminalViewHost(terminalView: session.currentTerminalView)
                .id(session.activeTerminalIdentity)
                .background(Color.black)
                .clipped()

            if let startupError = session.startupError {
                Text(startupError)
                    .font(.caption)
                    .foregroundStyle(.white)
                    .padding(8)
                    .background(.red.opacity(0.75), in: RoundedRectangle(cornerRadius: 8))
                    .padding(12)
            }
        }
    }
}
