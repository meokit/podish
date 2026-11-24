import SwiftUI

struct DesktopRootView: View {
    @ObservedObject var session: PodishDesktopSession
    @State private var toolbarExpanded = false
    @State private var showResetConfirm = false

    var body: some View {
        ZStack {
            #if os(macOS)
            MetalDesktopView(session: session)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            #else
            MetalDesktopView(session: session)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .ignoresSafeArea(.container, edges: .bottom)
                .ignoresSafeArea(.keyboard)
            #endif

            statusOverlay

            #if os(iOS)
            iosFloatingHelper
            #endif
        }
        .background(Color(red: 0.046, green: 0.056, blue: 0.089))
        .confirmationDialog(
            "Reset Debian user overlay?",
            isPresented: $showResetConfirm,
            titleVisibility: .visible
        ) {
            Button("Reset User Data", role: .destructive) {
                session.resetUserDataAndRestart()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This clears SilkFS modifications and restores the embedded Debian image to its initial state.")
        }
    }

    @ViewBuilder
    private var statusOverlay: some View {
        switch session.state {
        case .idle, .starting:
            let message: String = {
                if case .starting(let msg) = session.state, !msg.isEmpty { return msg }
                return "Starting Debian Desktop..."
            }()
            VStack(spacing: 14) {
                ProgressView()
                    .controlSize(.large)
                    .tint(.white)
                Text("Podish Debian Desktop")
                    .font(.headline)
                    .foregroundStyle(.white)
                Text(message)
                    .font(.subheadline)
                    .foregroundStyle(.white.opacity(0.72))
            }
            .padding(24)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
            .allowsHitTesting(false)

        case .failed(let err):
            VStack(spacing: 14) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 36))
                    .foregroundStyle(.yellow)
                Text("Desktop Failed to Start")
                    .font(.headline)
                    .foregroundStyle(.white)
                Text(err)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.85))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 380)
                HStack(spacing: 12) {
                    Button("Retry") { session.restart() }
                        .buttonStyle(.borderedProminent)
                    Button("Reset Overlay", role: .destructive) { showResetConfirm = true }
                        .buttonStyle(.bordered)
                }
            }
            .padding(24)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))

        case .exited(let reason):
            VStack(spacing: 12) {
                Text("Desktop Session Ended")
                    .font(.headline)
                    .foregroundStyle(.white)
                Text(reason)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.75))
                Button("Restart Desktop") { session.restart() }
                    .buttonStyle(.borderedProminent)
            }
            .padding(24)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))

        case .running:
            EmptyView()
        }
    }

    #if os(iOS)
    private var iosFloatingHelper: some View {
        VStack {
            Spacer()
            HStack {
                Spacer()
                if toolbarExpanded {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            helperButton(icon: "rectangle.and.hand.point.up.left.filled",
                                         label: "Pad",
                                         active: session.trackpadMode) {
                                session.toggleTrackpadMode()
                            }
                            helperButton(icon: "textformat.size",
                                         label: session.dpiPreset.shortLabel) {
                                session.cycleClientDpiPreset()
                            }
                            helperButton(icon: session.keyboardVisible ? "keyboard.chevron.compact.down" : "keyboard",
                                         label: "Keys",
                                         active: session.keyboardVisible) {
                                session.toggleKeyboard()
                            }
                            helperButton(icon: "computermouse.fill",
                                         label: "Right",
                                         active: session.rightClickMode) {
                                session.rightClickMode.toggle()
                            }
                            helperButton(icon: "escape", label: "Esc") {
                                session.sendKeyTap(evdevKey: 1)
                            }
                            helperButton(icon: "arrow.right.to.line", label: "Tab") {
                                session.sendKeyTap(evdevKey: 15)
                            }
                            helperButton(icon: "control", label: "Ctrl", active: session.ctrlSticky) {
                                session.ctrlSticky.toggle()
                            }
                            helperButton(icon: "arrow.counterclockwise", label: "Reset") {
                                showResetConfirm = true
                            }
                            Button {
                                withAnimation(.spring(duration: 0.22)) { toolbarExpanded = false }
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .font(.system(size: 20))
                                    .foregroundStyle(.white.opacity(0.85))
                                    .padding(6)
                            }
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                    }
                    .frame(maxWidth: 360)
                    .background(Color(red: 0.07, green: 0.11, blue: 0.20, opacity: 0.84), in: Capsule())
                    .overlay(Capsule().stroke(.white.opacity(0.22), lineWidth: 1))
                    .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
                } else {
                    HStack(spacing: 4) {
                        Button {
                            session.toggleTrackpadMode()
                        } label: {
                            Image(systemName: "rectangle.and.hand.point.up.left.filled")
                                .font(.system(size: 15, weight: .semibold))
                                .foregroundStyle(session.trackpadMode ? Color.cyan : .white)
                                .frame(width: 34, height: 34)
                        }
                        Button {
                            session.toggleKeyboard()
                        } label: {
                            Image(systemName: "keyboard")
                                .font(.system(size: 15, weight: .semibold))
                                .foregroundStyle(session.keyboardVisible ? Color.cyan : .white)
                                .frame(width: 34, height: 34)
                        }
                        Button {
                            withAnimation(.spring(duration: 0.22)) { toolbarExpanded = true }
                        } label: {
                            Image(systemName: "ellipsis.circle.fill")
                                .font(.system(size: 15, weight: .semibold))
                                .foregroundStyle(.white.opacity(0.9))
                                .frame(width: 34, height: 34)
                        }
                    }
                    .padding(.horizontal, 6)
                    .background(Color(red: 0.07, green: 0.11, blue: 0.20, opacity: 0.80), in: Capsule())
                    .overlay(Capsule().stroke(.white.opacity(0.22), lineWidth: 1))
                    .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
                }
            }
            .padding(.trailing, 12)
            .padding(.bottom, max(64, session.navBarHeightHost + 12))
        }
    }

    private func helperButton(icon: String, label: String, active: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 2) {
                Image(systemName: icon)
                    .font(.system(size: 14, weight: .semibold))
                Text(label)
                    .font(.system(size: 9, weight: .medium))
            }
            .foregroundStyle(active ? Color.accentColor : .white)
            .frame(minWidth: 38, minHeight: 36)
            .padding(.horizontal, 4)
            .background(active ? Color.accentColor.opacity(0.22) : Color.clear,
                        in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        }
    }
    #endif
}
