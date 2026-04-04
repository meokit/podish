import SwiftUI
#if os(iOS)
@preconcurrency import SwiftTerm
import UIKit
#endif

struct TerminalWorkspaceView: View {
    @ObservedObject var session: PodishTerminalSession

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            #if os(iOS)
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillShowNotification)) { note in
                logKeyboardEvent("keyboardWillShow", notification: note)
            }
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardDidShowNotification)) { note in
                logKeyboardEvent("keyboardDidShow", notification: note)
            }
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillHideNotification)) { note in
                logKeyboardEvent("keyboardWillHide", notification: note)
            }
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardDidHideNotification)) { note in
                logKeyboardEvent("keyboardDidHide", notification: note)
            }
            #endif
    }

    @ViewBuilder
    private var content: some View {
        #if os(iOS)
        terminalSurface
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(session.terminalBackgroundColor)
            .safeAreaInset(edge: .bottom, spacing: 0) {
                PodishVirtualKeysBar(
                    terminalView: session.currentTerminalView,
                    chromeBackgroundColor: session.terminalBackgroundColor
                )
                .padding(.horizontal, 8)
                .padding(.top, 6)
                .padding(.bottom, 8)
                .background(session.terminalBackgroundColor)
            }
        #else
        terminalSurface
        #endif
    }

    private var terminalSurface: some View {
        let terminalBackgroundColor: SwiftUI.Color = session.terminalBackgroundColor

        return ZStack(alignment: .topLeading) {
            TerminalViewHost(
                terminalView: session.currentTerminalView,
                shouldFocus: session.hasActiveTerminal
            )
                .id(session.activeTerminalIdentity)
                .background(terminalBackgroundColor)
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

    #if os(iOS)
    private func logKeyboardEvent(_ name: String, notification: Notification) {
        let duration = notification.userInfo?[UIResponder.keyboardAnimationDurationUserInfoKey] as? Double ?? -1
        let frame = notification.userInfo?[UIResponder.keyboardFrameEndUserInfoKey] as? CGRect ?? .zero
        let isFirstResponder = session.currentTerminalView.isFirstResponder
        PodishLog.ui("Workspace \(name) firstResponder=\(isFirstResponder) duration=\(duration) frame=\(String(describing: NSCoder.string(for: frame)))")
    }
    #endif
}

#if os(iOS)
private struct PodishVirtualKeysBar: View {
    let terminalView: TerminalView
    let chromeBackgroundColor: SwiftUI.Color

    @Environment(\.colorScheme) private var colorScheme

    @State private var controlActive = false
    @State private var controlLocked = false
    @State private var metaActive = false
    @State private var metaLocked = false

    var body: some View {
        VStack(spacing: 4) {
            keyRow([
                .escape,
                .slash,
                .dash,
                .home,
                .up,
                .end,
                .pageUp
            ])

            keyRow([
                .tab,
                .control,
                .alt,
                .left,
                .down,
                .right,
                .pageDown,
                .dismissKeyboard
            ])
        }
        .padding(6)
        .background(barBackgroundColor, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(barBorderColor)
        )
        .shadow(color: barShadowColor, radius: colorScheme == .dark ? 10 : 16, y: 4)
        .onAppear(perform: syncFromTerminalView)
        .onReceive(NotificationCenter.default.publisher(for: .terminalViewControlModifierReset, object: terminalView)) { _ in
            handleModifierReset(.control)
        }
        .onReceive(NotificationCenter.default.publisher(for: .terminalViewMetaModifierReset, object: terminalView)) { _ in
            handleModifierReset(.meta)
        }
        .onChange(of: ObjectIdentifier(terminalView)) { _ in
            syncFromTerminalView()
        }
    }

    private var barBackgroundColor: SwiftUI.Color {
        chromeBackgroundColor
    }

    private var barBorderColor: SwiftUI.Color {
        SwiftUI.Color(uiColor: colorScheme == .dark ? .separator.withAlphaComponent(0.35) : .separator.withAlphaComponent(0.18))
    }

    private var barShadowColor: SwiftUI.Color {
        .clear
    }

    private func keyRow(_ keys: [PodishVirtualKey]) -> some View {
        HStack(spacing: 4) {
            ForEach(keys) { key in
                PodishVirtualKeyButton(
                    key: key,
                    isActive: isActive(key),
                    onTap: { triggerKey(key) },
                    onLongPress: { lockKeyIfSupported(key) },
                    onPopup: { triggerPopupIfAvailable(key) }
                )
            }
        }
    }

    private func isActive(_ key: PodishVirtualKey) -> Bool {
        switch key {
        case .control:
            controlActive
        case .alt:
            metaActive
        default:
            false
        }
    }

    private func syncFromTerminalView() {
        controlActive = terminalView.controlModifier
        metaActive = terminalView.metaModifier
    }

    private func triggerKey(_ key: PodishVirtualKey) {
        ensureTerminalFocus()

        switch key {
        case .escape:
            sendSequence(EscapeSequences.cmdEsc)
        case .slash:
            terminalView.insertText("/")
        case .dash:
            terminalView.insertText("-")
        case .home:
            sendSequence(EscapeSequences.moveHomeNormal)
        case .up:
            sendSequence(EscapeSequences.moveUpNormal)
        case .end:
            sendSequence(EscapeSequences.moveEndNormal)
        case .pageUp:
            sendSequence(EscapeSequences.cmdPageUp)
        case .tab:
            terminalView.insertText("\t")
        case .control:
            toggleModifier(.control)
        case .alt:
            toggleModifier(.meta)
        case .left:
            sendHorizontalArrow(isLeft: true)
        case .down:
            sendSequence(EscapeSequences.moveDownNormal)
        case .right:
            sendHorizontalArrow(isLeft: false)
        case .pageDown:
            sendSequence(EscapeSequences.cmdPageDown)
        case .dismissKeyboard:
            _ = terminalView.resignFirstResponder()
        }
    }

    private func triggerPopupIfAvailable(_ key: PodishVirtualKey) {
        ensureTerminalFocus()
        switch key {
        case .slash:
            terminalView.insertText("\\")
        case .dash:
            terminalView.insertText("|")
        default:
            break
        }
    }

    private func toggleModifier(_ modifier: PodishModifier) {
        switch modifier {
        case .control:
            let nextValue = !controlActive
            controlLocked = false
            controlActive = nextValue
            terminalView.controlModifier = nextValue
        case .meta:
            let nextValue = !metaActive
            metaLocked = false
            metaActive = nextValue
            terminalView.metaModifier = nextValue
        }
    }

    private func lockKeyIfSupported(_ key: PodishVirtualKey) {
        ensureTerminalFocus()

        switch key {
        case .control:
            controlLocked = true
            controlActive = true
            terminalView.controlModifier = true
        case .alt:
            metaLocked = true
            metaActive = true
            terminalView.metaModifier = true
        default:
            break
        }
    }

    private func handleModifierReset(_ modifier: PodishModifier) {
        switch modifier {
        case .control:
            if controlLocked {
                terminalView.controlModifier = true
                controlActive = true
            } else {
                controlActive = false
            }
        case .meta:
            if metaLocked {
                terminalView.metaModifier = true
                metaActive = true
            } else {
                metaActive = false
            }
        }
    }

    private func ensureTerminalFocus() {
        if !terminalView.isFirstResponder {
            _ = terminalView.becomeFirstResponder()
        }
    }

    private func sendHorizontalArrow(isLeft: Bool) {
        var bytes = isLeft ? EscapeSequences.moveLeftNormal : EscapeSequences.moveRightNormal

        if controlActive {
            bytes = isLeft ? EscapeSequences.controlLeft : EscapeSequences.controlRight
        }

        sendSequence(bytes)
    }

    private func sendSequence(_ bytes: [UInt8]) {
        var payload: [UInt8] = []
        if metaActive {
            payload += EscapeSequences.cmdEsc
        }
        payload += bytes
        terminalView.send(payload)

        if controlActive && !controlLocked {
            controlActive = false
            terminalView.controlModifier = false
        }
        if metaActive && !metaLocked {
            metaActive = false
            terminalView.metaModifier = false
        }
    }
}

private enum PodishModifier {
    case control
    case meta
}

private enum PodishVirtualKey: String, CaseIterable, Identifiable {
    case escape
    case slash
    case dash
    case home
    case up
    case end
    case pageUp
    case tab
    case control
    case alt
    case left
    case down
    case right
    case pageDown
    case dismissKeyboard

    var id: String { rawValue }

    var title: String {
        switch self {
        case .escape:
            "ESC"
        case .slash:
            "/"
        case .dash:
            "\u{2015}"
        case .home:
            "HOME"
        case .up:
            "\u{2191}"
        case .end:
            "END"
        case .pageUp:
            "PGUP"
        case .tab:
            "\u{21B9}"
        case .control:
            "CTRL"
        case .alt:
            "ALT"
        case .left:
            "\u{2190}"
        case .down:
            "\u{2193}"
        case .right:
            "\u{2192}"
        case .pageDown:
            "PGDN"
        case .dismissKeyboard:
            ""
        }
    }

    var systemImage: String? {
        switch self {
        case .dismissKeyboard:
            "keyboard.chevron.compact.down"
        default:
            nil
        }
    }

    var popupHint: String? {
        switch self {
        case .slash:
            "\\"
        case .dash:
            "|"
        default:
            nil
        }
    }

    var supportsLongPressLock: Bool {
        self == .control || self == .alt
    }

    var repeatsWhenHeld: Bool {
        self == .up || self == .down || self == .left || self == .right
    }

    var buttonWidth: CGFloat {
        switch self {
        case .slash, .dash, .up, .left, .down, .right, .dismissKeyboard:
            34
        case .escape, .tab, .alt, .end:
            42
        case .home, .pageUp, .pageDown:
            50
        case .control:
            52
        }
    }
}

private struct PodishVirtualKeyButton: View {
    let key: PodishVirtualKey
    let isActive: Bool
    let onTap: () -> Void
    let onLongPress: () -> Void
    let onPopup: () -> Void

    @Environment(\.colorScheme) private var colorScheme

    @State private var isPressed = false
    @State private var didTriggerLongPressAction = false
    @State private var repeatTask: Task<Void, Never>?
    @State private var lockTask: Task<Void, Never>?

    var body: some View {
        RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(backgroundColor)
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(borderColor, lineWidth: 1)
            )
            .overlay {
                buttonContent
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
            }
            .overlay(alignment: .topTrailing) {
                if let popupHint = key.popupHint {
                    Text(popupHint)
                        .font(.system(size: 8, weight: .bold, design: .rounded))
                        .foregroundStyle(popupForegroundColor)
                        .padding(.top, 4)
                        .padding(.trailing, 5)
                }
            }
        .frame(width: key.buttonWidth, height: 36)
        .shadow(color: shadowColor, radius: colorScheme == .dark ? 0 : 1.5, y: 1)
        .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard !isPressed else { return }
                    beginPress()
                }
                .onEnded { value in
                    endPress(translationHeight: value.translation.height)
                }
        )
    }

    @ViewBuilder
    private var buttonContent: some View {
        if let systemImage = key.systemImage {
            Image(systemName: systemImage)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(foregroundColor)
        } else {
            Text(key.title)
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .lineLimit(1)
                .minimumScaleFactor(0.85)
                .foregroundStyle(foregroundColor)
                .padding(.horizontal, 6)
        }
    }

    private var backgroundColor: SwiftUI.Color {
        if isActive {
            return SwiftUI.Color(uiColor: isPressed ? .systemBlue : .tintColor)
        }

        if colorScheme == .dark {
            return SwiftUI.Color(uiColor: isPressed ? .tertiarySystemFill : .secondarySystemFill)
        }

        return SwiftUI.Color(uiColor: isPressed ? .secondarySystemFill : .tertiarySystemFill)
    }

    private var borderColor: SwiftUI.Color {
        if isActive {
            return SwiftUI.Color(uiColor: .tintColor).opacity(colorScheme == .dark ? 0.88 : 0.72)
        }

        return SwiftUI.Color(uiColor: colorScheme == .dark ? .separator.withAlphaComponent(0.28) : .separator.withAlphaComponent(0.14))
    }

    private var foregroundColor: SwiftUI.Color {
        if isActive {
            return .white
        }

        return SwiftUI.Color(uiColor: .label)
    }

    private var popupForegroundColor: SwiftUI.Color {
        if isActive {
            return SwiftUI.Color.white.opacity(0.72)
        }

        return SwiftUI.Color(uiColor: .secondaryLabel)
    }

    private var shadowColor: SwiftUI.Color {
        if colorScheme == .dark {
            return .clear
        }

        return SwiftUI.Color.black.opacity(isPressed ? 0.02 : 0.05)
    }

    private func beginPress() {
        isPressed = true
        didTriggerLongPressAction = false

        if key.repeatsWhenHeld {
            repeatTask = Task {
                try? await Task.sleep(nanoseconds: 400_000_000)
                guard !Task.isCancelled else { return }
                while !Task.isCancelled {
                    didTriggerLongPressAction = true
                    await MainActor.run { onTap() }
                    try? await Task.sleep(nanoseconds: 80_000_000)
                }
            }
        } else if key.supportsLongPressLock {
            lockTask = Task {
                try? await Task.sleep(nanoseconds: 450_000_000)
                guard !Task.isCancelled else { return }
                didTriggerLongPressAction = true
                await MainActor.run { onLongPress() }
            }
        }
    }

    private func endPress(translationHeight: CGFloat) {
        repeatTask?.cancel()
        repeatTask = nil
        lockTask?.cancel()
        lockTask = nil

        defer {
            isPressed = false
        }

        if translationHeight < -14, key.popupHint != nil {
            onPopup()
            return
        }

        if didTriggerLongPressAction {
            return
        }

        onTap()
    }
}
#endif
