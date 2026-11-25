import QuartzCore
import SwiftUI

#if os(macOS)
import AppKit

struct MetalDesktopView: NSViewRepresentable {
    @ObservedObject var session: PodishDesktopSession

    func makeNSView(context: Context) -> MacMetalDesktopNSView {
        let view = MacMetalDesktopNSView(session: session)
        return view
    }

    func updateNSView(_ nsView: MacMetalDesktopNSView, context: Context) {
        nsView.updateSession(session)
    }
}

final class MacMetalDesktopNSView: NSView, @preconcurrency NSTextInputClient {
    private var session: PodishDesktopSession
    private var trackingArea: NSTrackingArea?
    private var markedTextStorage = NSMutableAttributedString()
    private var markedRangeStorage = NSRange(location: NSNotFound, length: 0)
    private var selectedRangeStorage = NSRange(location: 0, length: 0)
    private var lastModifierFlags: NSEvent.ModifierFlags = []
    private var keyHandledByIme = false
    private var started = false

    init(session: PodishDesktopSession) {
        self.session = session
        super.init(frame: NSRect(x: 0, y: 0, width: 1280, height: 800))
        wantsLayer = true
        layerContentsRedrawPolicy = .duringViewResize
        configureCursorCallback()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) not supported")
    }

    override func makeBackingLayer() -> CALayer {
        let layer = CAMetalLayer()
        layer.pixelFormat = .bgra8Unorm
        layer.presentsWithTransaction = false
        layer.isOpaque = true
        return layer
    }

    private var metalLayer: CAMetalLayer? { layer as? CAMetalLayer }

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }

    func updateSession(_ session: PodishDesktopSession) {
        self.session = session
        configureCursorCallback()
    }

    private func configureCursorCallback() {
        session.onCursorChanged = { shape in
            switch shape {
            case 1: NSCursor.iBeam.set()
            case 2, 10: NSCursor.pointingHand.set()
            case 3: NSCursor.crosshair.set()
            case 4: NSCursor.openHand.set()
            case 5: NSCursor.resizeLeftRight.set()
            case 6: NSCursor.resizeUpDown.set()
            case 9: NSCursor.operationNotAllowed.set()
            default: NSCursor.arrow.set()
            }
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard window != nil else { return }
        window?.acceptsMouseMovedEvents = true
        window?.makeFirstResponder(self)
        syncLayerAndStartOrResize()
        session.sendFocusChanged(true)
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        syncLayerAndStartOrResize()
    }

    override func layout() {
        super.layout()
        syncLayerAndStartOrResize()
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea {
            removeTrackingArea(trackingArea)
        }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeInKeyWindow, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingArea = area
    }

    private func syncLayerAndStartOrResize() {
        guard let metalLayer, bounds.width >= 16, bounds.height >= 16 else { return }
        let scale = window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2.0
        metalLayer.contentsScale = scale
        let pixelSize = CGSize(
            width: max(1, (bounds.width * scale).rounded()),
            height: max(1, (bounds.height * scale).rounded())
        )
        if metalLayer.drawableSize != pixelSize {
            metalLayer.drawableSize = pixelSize
        }
        if !started {
            started = true
            session.attachAndStart(metalLayer: metalLayer, logicalSize: bounds.size, pixelSize: pixelSize, scale: scale)
        } else {
            session.resize(logicalSize: bounds.size, pixelSize: pixelSize, scale: scale)
        }
    }

    private func desktopPoint(for event: NSEvent) -> (Int32, Int32) {
        let p = convert(event.locationInWindow, from: nil)
        let x = Int32(max(0, min(bounds.width - 1, p.x)).rounded())
        let y = Int32(max(0, min(bounds.height - 1, p.y)).rounded())
        return (x, y)
    }

    override func mouseMoved(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerMotion(x: x, y: y)
    }

    override func mouseExited(with event: NSEvent) {
        session.sendPointerLeave()
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 0, pressed: true)
    }

    override func mouseUp(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 0, pressed: false)
    }

    override func mouseDragged(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerMotion(x: x, y: y)
    }

    override func rightMouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 1, pressed: true)
    }

    override func rightMouseUp(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 1, pressed: false)
    }

    override func rightMouseDragged(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerMotion(x: x, y: y)
    }

    override func otherMouseDown(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 2, pressed: true)
    }

    override func otherMouseUp(with event: NSEvent) {
        let (x, y) = desktopPoint(for: event)
        session.sendPointerButton(x: x, y: y, button: 2, pressed: false)
    }

    override func scrollWheel(with event: NSEvent) {
        let dx: Int32
        let dy: Int32
        if event.hasPreciseScrollingDeltas {
            dx = Int32((-event.scrollingDeltaX).rounded())
            dy = Int32((-event.scrollingDeltaY).rounded())
        } else {
            dx = Int32((-event.scrollingDeltaX * 15.0).rounded())
            dy = Int32((-event.scrollingDeltaY * 15.0).rounded())
        }
        if dx != 0 || dy != 0 {
            session.sendPointerWheel(dx: dx, dy: dy)
        }
    }

    override func keyDown(with event: NSEvent) {
        keyHandledByIme = false
        let hasCtrlOrCmd = event.modifierFlags.contains(.control) || event.modifierFlags.contains(.command)
        if session.textInputActive && !hasCtrlOrCmd {
            interpretKeyEvents([event])
        }
        if let evdev = PodishEvdevKeyMap.fromMacKeyCode(event.keyCode) {
            session.sendKey(evdevKey: evdev, pressed: true, handledByIme: keyHandledByIme)
        }
    }

    override func keyUp(with event: NSEvent) {
        if let evdev = PodishEvdevKeyMap.fromMacKeyCode(event.keyCode) {
            session.sendKey(evdevKey: evdev, pressed: false, handledByIme: false)
        }
    }

    override func flagsChanged(with event: NSEvent) {
        let flags = event.modifierFlags
        let map: [(NSEvent.ModifierFlags, UInt32)] = [
            (.shift, 42),
            (.control, 29),
            (.option, 56),
            (.command, 125),
            (.capsLock, 58)
        ]
        for (flag, evdev) in map {
            let wasOn = lastModifierFlags.contains(flag)
            let isOn = flags.contains(flag)
            if wasOn != isOn {
                session.sendKey(evdevKey: evdev, pressed: isOn, handledByIme: false)
            }
        }
        lastModifierFlags = flags
    }

    // MARK: NSTextInputClient

    func insertText(_ string: Any, replacementRange: NSRange) {
        let text: String
        if let attr = string as? NSAttributedString {
            text = attr.string
        } else if let str = string as? String {
            text = str
        } else {
            return
        }
        markedTextStorage = NSMutableAttributedString()
        markedRangeStorage = NSRange(location: NSNotFound, length: 0)
        if !text.isEmpty {
            keyHandledByIme = true
            session.sendTextInput(text)
        }
    }

    func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        let text: String
        if let attr = string as? NSAttributedString {
            text = attr.string
        } else if let str = string as? String {
            text = str
        } else {
            text = ""
        }
        markedTextStorage = NSMutableAttributedString(string: text)
        markedRangeStorage = text.isEmpty
            ? NSRange(location: NSNotFound, length: 0)
            : NSRange(location: 0, length: (text as NSString).length)
        selectedRangeStorage = selectedRange
        keyHandledByIme = true
        session.sendTextEditingUtf16(text, selectedUtf16Range: selectedRange)
    }

    func unmarkText() {
        markedTextStorage = NSMutableAttributedString()
        markedRangeStorage = NSRange(location: NSNotFound, length: 0)
        session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
    }

    func selectedRange() -> NSRange { selectedRangeStorage }
    func markedRange() -> NSRange { markedRangeStorage }
    func hasMarkedText() -> Bool { markedRangeStorage.location != NSNotFound }

    func attributedSubstring(forProposedRange range: NSRange, actualRange: NSRangePointer?) -> NSAttributedString? {
        nil
    }

    func validAttributesForMarkedText() -> [NSAttributedString.Key] { [] }

    func firstRect(forCharacterRange range: NSRange, actualRange: NSRangePointer?) -> NSRect {
        let r = session.hasValidTextInputRect ? session.textInputRect : CGRect(x: 0, y: 0, width: 1, height: 20)
        let localRect = NSRect(x: r.origin.x, y: r.origin.y, width: max(1, r.width), height: max(20, r.height))
        let winRect = convert(localRect, to: nil)
        return window?.convertToScreen(winRect) ?? winRect
    }

    func characterIndex(for point: NSPoint) -> Int { NSNotFound }

    override func doCommand(by selector: Selector) {
        // Let non-text commands (Return, Backspace, Escape, arrows) pass through via evdev keyDown.
    }
}

#else
import UIKit

struct MetalDesktopView: UIViewRepresentable {
    @ObservedObject var session: PodishDesktopSession

    func makeUIView(context: Context) -> IOSDesktopViewportContainerView {
        IOSDesktopViewportContainerView(session: session)
    }

    func updateUIView(_ uiView: IOSDesktopViewportContainerView, context: Context) {
        uiView.updateSession(session)
    }
}

// MARK: - iOS Viewport Container (Smooth Keyboard Avoidance Without Resizing Wayland)

final class IOSDesktopViewportContainerView: UIView {
    private var session: PodishDesktopSession
    let metalView: IOSMetalDesktopUIView

    private var keyboardFrameInContainer: CGRect = .zero
    private var keyboardVisible = false
    private var currentPanOffsetY: CGFloat = 0

    init(session: PodishDesktopSession) {
        self.session = session
        self.metalView = IOSMetalDesktopUIView(session: session)
        super.init(frame: .zero)
        clipsToBounds = true
        backgroundColor = UIColor(red: 0.046, green: 0.056, blue: 0.089, alpha: 1.0)
        addSubview(metalView)

        metalView.onCursorOrFocusChanged = { [weak self] in
            self?.recalculateKeyboardPan(animated: true, duration: 0.22, curveRaw: UIView.AnimationCurve.easeOut.rawValue)
        }

        let nc = NotificationCenter.default
        nc.addObserver(self, selector: #selector(handleKeyboardWillChangeFrame(_:)),
                       name: UIResponder.keyboardWillChangeFrameNotification, object: nil)
        nc.addObserver(self, selector: #selector(handleKeyboardWillHide(_:)),
                       name: UIResponder.keyboardWillHideNotification, object: nil)
        nc.addObserver(self, selector: #selector(handleKeyboardDidHide(_:)),
                       name: UIResponder.keyboardDidHideNotification, object: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) not supported")
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    func updateSession(_ session: PodishDesktopSession) {
        self.session = session
        metalView.updateSession(session)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard bounds.width >= 16, bounds.height >= 16 else { return }
        // Keep metalView full-sized so keyboard show/hide never triggers Wayland desktop resize thrashing.
        recalculateKeyboardPan(animated: false, duration: 0, curveRaw: 0)
        let targetFrame = CGRect(x: 0, y: -currentPanOffsetY, width: bounds.width, height: bounds.height)
        if metalView.frame != targetFrame {
            metalView.frame = targetFrame
        }
    }

    @objc private func handleKeyboardWillChangeFrame(_ notification: Notification) {
        guard let userInfo = notification.userInfo,
              let endFrameScreen = userInfo[UIResponder.keyboardFrameEndUserInfoKey] as? CGRect else {
            return
        }
        let duration = (userInfo[UIResponder.keyboardAnimationDurationUserInfoKey] as? TimeInterval) ?? 0.25
        let curveRaw = (userInfo[UIResponder.keyboardAnimationCurveUserInfoKey] as? Int) ?? UIView.AnimationCurve.easeInOut.rawValue

        let kbInContainer = convertScreenRectToContainer(endFrameScreen)
        let overlap = bounds.intersection(kbInContainer)
        keyboardFrameInContainer = overlap.isNull ? .zero : overlap
        keyboardVisible = !overlap.isNull && overlap.height > 1 && overlap.minY < bounds.height - 1

        recalculateKeyboardPan(animated: duration > 0.001, duration: duration, curveRaw: curveRaw)
    }

    @objc private func handleKeyboardWillHide(_ notification: Notification) {
        let userInfo = notification.userInfo
        let duration = (userInfo?[UIResponder.keyboardAnimationDurationUserInfoKey] as? TimeInterval) ?? 0.25
        let curveRaw = (userInfo?[UIResponder.keyboardAnimationCurveUserInfoKey] as? Int) ?? UIView.AnimationCurve.easeInOut.rawValue
        keyboardFrameInContainer = .zero
        keyboardVisible = false
        recalculateKeyboardPan(animated: duration > 0.001, duration: duration, curveRaw: curveRaw)
    }

    @objc private func handleKeyboardDidHide(_ notification: Notification) {
        keyboardFrameInContainer = .zero
        keyboardVisible = false
        recalculateKeyboardPan(animated: false, duration: 0, curveRaw: 0)
    }

    private func convertScreenRectToContainer(_ screenRect: CGRect) -> CGRect {
        guard let window else { return .zero }
        return convert(screenRect, from: window.screen.coordinateSpace)
    }

    private func computeDesiredPanOffsetY() -> CGFloat {
        guard bounds.height > 0, keyboardVisible, !keyboardFrameInContainer.isEmpty else {
            return 0
        }

        // Determine target input rectangle in host container points (mapped from desktop logical coords).
        let targetRect: CGRect
        if session.hasValidTextInputRect {
            targetRect = session.desktopRectToHost(session.textInputRect, in: bounds.size)
        } else if let tap = metalView.lastTapHostPoint {
            targetRect = CGRect(x: tap.x, y: max(0, tap.y - 16), width: 2, height: 32)
        } else {
            return 0
        }

        // Support both docked bottom keyboard and floating iPad keyboard.
        let touchesBottom = keyboardFrameInContainer.maxY >= bounds.height - 24
        let keyboardOverlap: CGFloat
        if touchesBottom {
            keyboardOverlap = max(0, bounds.height - keyboardFrameInContainer.minY)
        } else if keyboardFrameInContainer.intersects(targetRect) {
            keyboardOverlap = max(0, bounds.height - keyboardFrameInContainer.minY)
        } else {
            return 0
        }

        let bottomMargin: CGFloat = 44
        let visibleBottom = bounds.height - keyboardOverlap - bottomMargin
        guard targetRect.maxY > visibleBottom else {
            return 0
        }

        let needed = targetRect.maxY - visibleBottom
        let topLimit = safeAreaInsets.top + 12
        let maxWithoutClippingTop = max(0, targetRect.minY - topLimit)
        let maxPan = min(keyboardOverlap, maxWithoutClippingTop)
        return min(max(0, needed), maxPan).rounded()
    }

    private func recalculateKeyboardPan(animated: Bool, duration: TimeInterval, curveRaw: Int) {
        let desired = computeDesiredPanOffsetY()
        guard abs(desired - currentPanOffsetY) >= 0.5 else { return }
        currentPanOffsetY = desired

        let applyFrame = { [weak self] in
            guard let self else { return }
            self.metalView.frame = CGRect(x: 0, y: -self.currentPanOffsetY, width: self.bounds.width, height: self.bounds.height)
        }

        if animated && duration > 0.001 {
            let options = UIView.AnimationOptions(rawValue: UInt(curveRaw) << 16).union([.beginFromCurrentState, .allowUserInteraction])
            UIView.animate(withDuration: duration, delay: 0, options: options, animations: applyFrame)
        } else {
            applyFrame()
        }
    }
}

// MARK: - UITextInput Position & Range Helpers

final class PodishTextPosition: UITextPosition {
    let offset: Int
    init(_ offset: Int) {
        self.offset = max(0, offset)
        super.init()
    }
}

final class PodishTextRange: UITextRange {
    let range: NSRange

    init(_ range: NSRange) {
        self.range = NSRange(location: max(0, range.location), length: max(0, range.length))
        super.init()
    }

    override var start: UITextPosition { PodishTextPosition(range.location) }
    override var end: UITextPosition { PodishTextPosition(range.location + range.length) }
    override var isEmpty: Bool { range.length == 0 }
}

// MARK: - iOS Metal Desktop View with Full UITextInput IME Support

final class IOSMetalDesktopUIView: UIView, UITextInput {
    private static let maxRollingBufferLength = 64

    private var session: PodishDesktopSession
    private var started = false
    private var activeSingleTouch: UITouch?
    private var twoFingerScrollActive = false
    private var lastTwoFingerMidpoint: CGPoint = .zero
    private var twoFingerMoved = false
    private var rightButtonDown = false
    private var manualKeyboardRequested = false
    private var userDismissedKeyboardForCurrentSession = false
    private var wasTextInputActive = false
    private var pendingResignWorkItem: DispatchWorkItem?

    // Trackpad simulation state (relative cursor control for desktop mouse-oriented apps)
    private var trackpadCursor = CGPoint(x: 240, y: 320)
    private var trackpadCursorInitialized = false
    private var trackpadActiveTouch: UITouch?
    private var trackpadStartHost: CGPoint = .zero
    private var trackpadLastHost: CGPoint = .zero
    private var trackpadMoved = false
    private var trackpadDownTime: TimeInterval = 0
    private var lastTrackpadTapTime: TimeInterval = 0
    private var trackpadDraggingLeft = false

    private(set) var lastTapHostPoint: CGPoint?
    var onCursorOrFocusChanged: (() -> Void)?

    // Local composition & rolling suffix buffer required by UIKit's CJK IME (Pinyin/Wubi/Kana/Hangul).
    private let textStorage = NSMutableString()
    private var selectedRangeStorage = NSRange(location: 0, length: 0)
    private var markedRangeStorage: NSRange?

    // UITextInput properties
    weak var inputDelegate: (any UITextInputDelegate)?
    var markedTextStyle: [NSAttributedString.Key: Any]?
    lazy var tokenizer: any UITextInputTokenizer = UITextInputStringTokenizer(textInput: self)

    // UITextInputTraits
    var keyboardType: UIKeyboardType = .default
    var returnKeyType: UIReturnKeyType = .default
    var autocorrectionType: UITextAutocorrectionType = .no
    var autocapitalizationType: UITextAutocapitalizationType = .none
    var spellCheckingType: UITextSpellCheckingType = .no
    var smartQuotesType: UITextSmartQuotesType = .no
    var smartDashesType: UITextSmartDashesType = .no
    var smartInsertDeleteType: UITextSmartInsertDeleteType = .no

    override class var layerClass: AnyClass { CAMetalLayer.self }
    private var metalLayer: CAMetalLayer { layer as! CAMetalLayer }

    init(session: PodishDesktopSession) {
        self.session = session
        super.init(frame: .zero)
        isMultipleTouchEnabled = true
        isUserInteractionEnabled = true
        backgroundColor = UIColor(red: 0.046, green: 0.056, blue: 0.089, alpha: 1.0)
        metalLayer.pixelFormat = .bgra8Unorm
        metalLayer.isOpaque = true
        configureCallbacks()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) not supported")
    }

    override var canBecomeFirstResponder: Bool { true }
    // Always report true so iOS keeps the Backspace key enabled for text inside Wayland apps.
    var hasText: Bool { true }

    @discardableResult
    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok {
            userDismissedKeyboardForCurrentSession = false
            session.setKeyboardVisible(true)
            onCursorOrFocusChanged?()
        }
        return ok
    }

    @discardableResult
    override func resignFirstResponder() -> Bool {
        if session.textInputActive {
            // User explicitly dismissed the keyboard while the guest text field remained focused;
            // suppress auto re-opening on subsequent cursor-rect updates until user taps or focuses a new field.
            userDismissedKeyboardForCurrentSession = true
        }
        if let marked = markedRangeStorage {
            let markedStr = clampedSubstring(marked)
            markedRangeStorage = nil
            if !markedStr.isEmpty {
                session.sendTextInput(markedStr)
            } else {
                session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
            }
        }
        let ok = super.resignFirstResponder()
        if ok {
            manualKeyboardRequested = false
            session.setKeyboardVisible(false)
            onCursorOrFocusChanged?()
        }
        return ok
    }

    func updateSession(_ session: PodishDesktopSession) {
        self.session = session
        configureCallbacks()
    }

    private func configureCallbacks() {
        session.onToggleKeyboardRequested = { [weak self] in
            guard let self else { return }
            self.pendingResignWorkItem?.cancel()
            self.pendingResignWorkItem = nil
            if self.isFirstResponder {
                self.manualKeyboardRequested = false
                self.userDismissedKeyboardForCurrentSession = true
                self.resignFirstResponder()
            } else {
                self.userDismissedKeyboardForCurrentSession = false
                self.manualKeyboardRequested = true
                _ = self.becomeFirstResponder()
            }
        }

        session.onTextInputChanged = { [weak self] active, _ in
            guard let self else { return }
            let newlyActivated = active && !self.wasTextInputActive
            self.wasTextInputActive = active
            self.pendingResignWorkItem?.cancel()
            self.pendingResignWorkItem = nil

            if active {
                if newlyActivated {
                    self.userDismissedKeyboardForCurrentSession = false
                }
                if !self.isFirstResponder && !self.userDismissedKeyboardForCurrentSession {
                    _ = self.becomeFirstResponder()
                }
                self.onCursorOrFocusChanged?()
            } else {
                // Debounce slightly (90ms) to absorb Wayland zwp_text_input_v3 restart/re-enable bursts
                // without flickering the iOS software keyboard.
                let work = DispatchWorkItem { [weak self] in
                    guard let self else { return }
                    self.pendingResignWorkItem = nil
                    if !self.session.textInputActive {
                        self.userDismissedKeyboardForCurrentSession = false
                        self.resetLocalCompositionBuffer(notifyDelegate: true)
                        if !self.manualKeyboardRequested && self.isFirstResponder {
                            self.resignFirstResponder()
                        } else {
                            self.onCursorOrFocusChanged?()
                        }
                    }
                }
                self.pendingResignWorkItem = work
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.09, execute: work)
            }
        }
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard window != nil else { return }
        syncLayerAndStartOrResize()
        session.sendFocusChanged(true)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        syncLayerAndStartOrResize()
    }

    private func syncLayerAndStartOrResize() {
        guard bounds.width >= 16, bounds.height >= 16 else { return }
        let scale = window?.screen.scale ?? UIScreen.main.scale
        contentScaleFactor = scale
        metalLayer.contentsScale = scale
        let pixelSize = CGSize(
            width: max(1, (bounds.width * scale).rounded()),
            height: max(1, (bounds.height * scale).rounded())
        )
        if metalLayer.drawableSize != pixelSize {
            metalLayer.drawableSize = pixelSize
        }
        if !started {
            started = true
            session.attachAndStart(metalLayer: metalLayer, logicalSize: bounds.size, pixelSize: pixelSize, scale: scale)
        } else {
            session.resize(logicalSize: bounds.size, pixelSize: pixelSize, scale: scale)
        }
    }

    private func desktopPoint(for point: CGPoint) -> (Int32, Int32) {
        session.hostToDesktop(point, in: bounds.size)
    }

    private func ensureTrackpadCursorInitialized() {
        let dw = session.desktopLogicalSize.width > 1 ? session.desktopLogicalSize.width : bounds.width
        let dh = session.desktopLogicalSize.height > 1 ? session.desktopLogicalSize.height : bounds.height
        let usableH = max(1, dh - session.navBarHeightDesktop)
        if !trackpadCursorInitialized {
            trackpadCursorInitialized = true
            trackpadCursor = CGPoint(x: dw * 0.5, y: usableH * 0.45)
        } else {
            trackpadCursor.x = max(0, min(dw - 1, trackpadCursor.x))
            trackpadCursor.y = max(0, min(dh - 1, trackpadCursor.y))
        }
    }

    private func isPointInNavBar(_ hostPoint: CGPoint) -> Bool {
        let (_, dy) = desktopPoint(for: hostPoint)
        let dh = session.desktopLogicalSize.height > 1 ? session.desktopLogicalSize.height : bounds.height
        return CGFloat(dy) >= dh - session.navBarHeightDesktop
    }

    // MARK: Touch Input (Direct Touch + Mobile Trackpad Simulation + Two-finger Scroll & Right-click)

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        let allTouches = event?.allTouches?.filter { $0.phase != .ended && $0.phase != .cancelled } ?? []
        if allTouches.count >= 2 {
            if let single = activeSingleTouch {
                let (x, y) = desktopPoint(for: single.location(in: self))
                session.sendPointerButton(x: x, y: y, button: rightButtonDown ? 1 : 0, pressed: false)
                activeSingleTouch = nil
                rightButtonDown = false
            }
            if trackpadActiveTouch != nil {
                if trackpadDraggingLeft {
                    ensureTrackpadCursorInitialized()
                    session.sendPointerButton(x: Int32(trackpadCursor.x.rounded()),
                                              y: Int32(trackpadCursor.y.rounded()),
                                              button: 0, pressed: false)
                    trackpadDraggingLeft = false
                }
                trackpadActiveTouch = nil
            }
            let pts = allTouches.prefix(2).map { $0.location(in: self) }
            lastTwoFingerMidpoint = CGPoint(x: (pts[0].x + pts[1].x) * 0.5, y: (pts[0].y + pts[1].y) * 0.5)
            twoFingerScrollActive = true
            twoFingerMoved = false
            return
        }

        guard let touch = touches.first else { return }
        let loc = touch.location(in: self)
        userDismissedKeyboardForCurrentSession = false

        // Always allow direct taps on the compositor's bottom Android 3-button navbar even in Trackpad mode.
        if session.trackpadMode && !isPointInNavBar(loc) {
            ensureTrackpadCursorInitialized()
            trackpadActiveTouch = touch
            trackpadStartHost = loc
            trackpadLastHost = loc
            trackpadMoved = false
            let now = ProcessInfo.processInfo.systemUptime
            trackpadDownTime = now
            if now - lastTrackpadTapTime < 0.28 {
                trackpadDraggingLeft = true
                let cx = Int32(trackpadCursor.x.rounded())
                let cy = Int32(trackpadCursor.y.rounded())
                session.sendPointerMotion(x: cx, y: cy)
                session.sendPointerButton(x: cx, y: cy, button: 0, pressed: true)
            } else {
                trackpadDraggingLeft = false
            }
            return
        }

        activeSingleTouch = touch
        lastTapHostPoint = loc
        if markedRangeStorage != nil || textStorage.length > 0 {
            if markedRangeStorage != nil {
                session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
            }
            resetLocalCompositionBuffer(notifyDelegate: true)
        }
        let (x, y) = desktopPoint(for: loc)
        let useRight = session.rightClickMode
        rightButtonDown = useRight
        session.sendPointerMotion(x: x, y: y)
        session.sendPointerButton(x: x, y: y, button: useRight ? 1 : 0, pressed: true)
        if useRight {
            session.rightClickMode = false
        }
        if isFirstResponder && !session.hasValidTextInputRect {
            onCursorOrFocusChanged?()
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        let allTouches = event?.allTouches?.filter { $0.phase != .ended && $0.phase != .cancelled } ?? []
        if twoFingerScrollActive && allTouches.count >= 2 {
            let pts = allTouches.prefix(2).map { $0.location(in: self) }
            let mid = CGPoint(x: (pts[0].x + pts[1].x) * 0.5, y: (pts[0].y + pts[1].y) * 0.5)
            let dx = mid.x - lastTwoFingerMidpoint.x
            let dy = mid.y - lastTwoFingerMidpoint.y
            if abs(dx) > 1.5 || abs(dy) > 1.5 {
                twoFingerMoved = true
                lastTwoFingerMidpoint = mid
                if session.trackpadMode {
                    ensureTrackpadCursorInitialized()
                    session.sendPointerMotion(x: Int32(trackpadCursor.x.rounded()),
                                              y: Int32(trackpadCursor.y.rounded()))
                }
                session.sendPointerWheel(dx: Int32((-dx * 1.5).rounded()), dy: Int32((-dy * 1.5).rounded()))
            }
            return
        }

        if let tp = trackpadActiveTouch, touches.contains(tp) {
            ensureTrackpadCursorInitialized()
            let loc = tp.location(in: self)
            let dxHost = loc.x - trackpadLastHost.x
            let dyHost = loc.y - trackpadLastHost.y
            trackpadLastHost = loc

            let totalDist = hypot(loc.x - trackpadStartHost.x, loc.y - trackpadStartHost.y)
            if totalDist > 6.0 {
                trackpadMoved = true
            }

            let dw = session.desktopLogicalSize.width > 1 ? session.desktopLogicalSize.width : bounds.width
            let dh = session.desktopLogicalSize.height > 1 ? session.desktopLogicalSize.height : bounds.height
            let scaleX = bounds.width > 0 ? dw / bounds.width : 1.0
            let scaleY = bounds.height > 0 ? dh / bounds.height : 1.0
            let speed = hypot(dxHost, dyHost)
            let accel = min(2.1, 1.2 + speed * 0.045)
            trackpadCursor.x = max(0, min(dw - 1, trackpadCursor.x + dxHost * scaleX * accel))
            trackpadCursor.y = max(0, min(dh - 1, trackpadCursor.y + dyHost * scaleY * accel))
            session.sendPointerMotion(x: Int32(trackpadCursor.x.rounded()),
                                      y: Int32(trackpadCursor.y.rounded()))
            return
        }

        if let single = activeSingleTouch, touches.contains(single) {
            let (x, y) = desktopPoint(for: single.location(in: self))
            session.sendPointerMotion(x: x, y: y)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if twoFingerScrollActive {
            let remaining = event?.allTouches?.filter { $0.phase != .ended && $0.phase != .cancelled } ?? []
            if remaining.isEmpty {
                if !twoFingerMoved {
                    let x: Int32
                    let y: Int32
                    if session.trackpadMode {
                        ensureTrackpadCursorInitialized()
                        x = Int32(trackpadCursor.x.rounded())
                        y = Int32(trackpadCursor.y.rounded())
                    } else {
                        (x, y) = desktopPoint(for: lastTwoFingerMidpoint)
                    }
                    session.sendPointerMotion(x: x, y: y)
                    session.sendPointerButton(x: x, y: y, button: 1, pressed: true)
                    session.sendPointerButton(x: x, y: y, button: 1, pressed: false)
                }
                twoFingerScrollActive = false
            }
            return
        }

        if let tp = trackpadActiveTouch, touches.contains(tp) {
            ensureTrackpadCursorInitialized()
            let cx = Int32(trackpadCursor.x.rounded())
            let cy = Int32(trackpadCursor.y.rounded())
            if trackpadDraggingLeft {
                session.sendPointerButton(x: cx, y: cy, button: 0, pressed: false)
                trackpadDraggingLeft = false
            } else if !trackpadMoved {
                let now = ProcessInfo.processInfo.systemUptime
                if now - trackpadDownTime < 0.35 {
                    let useRight = session.rightClickMode
                    let btn: UInt32 = useRight ? 1 : 0
                    if useRight {
                        session.rightClickMode = false
                    }
                    let hostCursor = session.desktopRectToHost(CGRect(x: trackpadCursor.x, y: trackpadCursor.y, width: 1, height: 1), in: bounds.size).origin
                    lastTapHostPoint = hostCursor
                    if markedRangeStorage != nil || textStorage.length > 0 {
                        if markedRangeStorage != nil {
                            session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
                        }
                        resetLocalCompositionBuffer(notifyDelegate: true)
                    }
                    session.sendPointerMotion(x: cx, y: cy)
                    session.sendPointerButton(x: cx, y: cy, button: btn, pressed: true)
                    session.sendPointerButton(x: cx, y: cy, button: btn, pressed: false)
                    lastTrackpadTapTime = now
                }
            }
            trackpadActiveTouch = nil
            return
        }

        if let single = activeSingleTouch, touches.contains(single) {
            let (x, y) = desktopPoint(for: single.location(in: self))
            session.sendPointerButton(x: x, y: y, button: rightButtonDown ? 1 : 0, pressed: false)
            activeSingleTouch = nil
            rightButtonDown = false
        }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        touchesEnded(touches, with: event)
    }

    // MARK: Local Composition Buffer Helpers

    private func clampRange(_ range: NSRange) -> NSRange {
        let len = textStorage.length
        let loc = max(0, min(range.location, len))
        let clampedLen = max(0, min(range.length, len - loc))
        return NSRange(location: loc, length: clampedLen)
    }

    private func clampedSubstring(_ range: NSRange) -> String {
        let clamped = clampRange(range)
        guard clamped.length > 0 else { return "" }
        return textStorage.substring(with: clamped)
    }

    private func trimRollingBufferIfNeeded() {
        guard markedRangeStorage == nil else { return }
        let excess = textStorage.length - Self.maxRollingBufferLength
        guard excess > 0 else { return }
        let safeCut = textStorage.rangeOfComposedCharacterSequence(at: excess).location
        guard safeCut > 0 else { return }
        textStorage.deleteCharacters(in: NSRange(location: 0, length: safeCut))
        let newLoc = max(0, selectedRangeStorage.location - safeCut)
        selectedRangeStorage = NSRange(location: newLoc, length: min(selectedRangeStorage.length, textStorage.length - newLoc))
    }

    private func resetLocalCompositionBuffer(notifyDelegate: Bool) {
        if notifyDelegate {
            inputDelegate?.selectionWillChange(self)
            inputDelegate?.textWillChange(self)
        }
        textStorage.setString("")
        markedRangeStorage = nil
        selectedRangeStorage = NSRange(location: 0, length: 0)
        if notifyDelegate {
            inputDelegate?.textDidChange(self)
            inputDelegate?.selectionDidChange(self)
        }
    }

    // MARK: UIKeyInput & UITextInput

    func insertText(_ text: String) {
        let hadMarked = markedRangeStorage != nil
        let target = clampRange(markedRangeStorage ?? selectedRangeStorage)
        textStorage.replaceCharacters(in: target, with: text)
        markedRangeStorage = nil
        let newCaret = target.location + (text as NSString).length
        selectedRangeStorage = NSRange(location: newCaret, length: 0)
        trimRollingBufferIfNeeded()

        if text == "\n" {
            if hadMarked {
                session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
            }
            session.sendKeyTap(evdevKey: 28)
        } else if !text.isEmpty {
            session.sendTextInput(text)
        } else if hadMarked {
            session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
        }
    }

    func deleteBackward() {
        if let marked = markedRangeStorage, marked.length > 0 {
            let clamped = clampRange(marked)
            let lastIdx = clamped.location + clamped.length - 1
            let charRange = textStorage.rangeOfComposedCharacterSequence(at: lastIdx)
            let deleteRange = NSIntersectionRange(clamped, charRange)
            if deleteRange.length > 0 {
                textStorage.deleteCharacters(in: deleteRange)
                let newLen = clamped.length - deleteRange.length
                if newLen > 0 {
                    let newMarked = NSRange(location: clamped.location, length: newLen)
                    markedRangeStorage = newMarked
                    selectedRangeStorage = NSRange(location: newMarked.location + newMarked.length, length: 0)
                    let preedit = clampedSubstring(newMarked)
                    session.sendTextEditingUtf16(preedit, selectedUtf16Range: NSRange(location: newLen, length: 0))
                } else {
                    markedRangeStorage = nil
                    selectedRangeStorage = NSRange(location: clamped.location, length: 0)
                    session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
                }
                return
            }
        }

        if selectedRangeStorage.length > 0 {
            let clamped = clampRange(selectedRangeStorage)
            let count = max(1, clampedSubstring(clamped).count)
            textStorage.deleteCharacters(in: clamped)
            selectedRangeStorage = NSRange(location: clamped.location, length: 0)
            for _ in 0..<count {
                session.sendKeyTap(evdevKey: 14)
            }
            return
        }

        if selectedRangeStorage.location > 0 && textStorage.length > 0 {
            let prevIdx = min(selectedRangeStorage.location - 1, textStorage.length - 1)
            let charRange = textStorage.rangeOfComposedCharacterSequence(at: prevIdx)
            textStorage.deleteCharacters(in: charRange)
            selectedRangeStorage = NSRange(location: charRange.location, length: 0)
        }
        session.sendKeyTap(evdevKey: 14)
    }

    func setMarkedText(_ markedText: String?, selectedRange: NSRange) {
        let text = markedText ?? ""
        let target = clampRange(markedRangeStorage ?? selectedRangeStorage)
        textStorage.replaceCharacters(in: target, with: text)
        let textLen = (text as NSString).length

        if textLen > 0 {
            let newMarked = NSRange(location: target.location, length: textLen)
            markedRangeStorage = newMarked
            let relLoc = selectedRange.location == NSNotFound
                ? textLen
                : max(0, min(selectedRange.location, textLen))
            let relLen = selectedRange.location == NSNotFound
                ? 0
                : max(0, min(selectedRange.length, textLen - relLoc))
            selectedRangeStorage = NSRange(location: target.location + relLoc, length: relLen)
            session.sendTextEditingUtf16(text, selectedUtf16Range: NSRange(location: relLoc, length: relLen))
        } else {
            markedRangeStorage = nil
            selectedRangeStorage = NSRange(location: target.location, length: 0)
            trimRollingBufferIfNeeded()
            session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
        }
    }

    func unmarkText() {
        guard let marked = markedRangeStorage else { return }
        let clamped = clampRange(marked)
        let text = clampedSubstring(clamped)
        markedRangeStorage = nil
        selectedRangeStorage = NSRange(location: clamped.location + clamped.length, length: 0)
        trimRollingBufferIfNeeded()

        if !text.isEmpty {
            session.sendTextInput(text)
        } else {
            session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
        }
    }

    func text(in range: UITextRange) -> String? {
        guard let r = range as? PodishTextRange else { return nil }
        return clampedSubstring(r.range)
    }

    func replace(_ range: UITextRange, withText text: String) {
        guard let r = range as? PodishTextRange else {
            insertText(text)
            return
        }
        let target = clampRange(r.range)
        if let marked = markedRangeStorage, NSEqualRanges(clampRange(marked), target) {
            textStorage.replaceCharacters(in: target, with: text)
            markedRangeStorage = nil
            selectedRangeStorage = NSRange(location: target.location + (text as NSString).length, length: 0)
            trimRollingBufferIfNeeded()
            if !text.isEmpty {
                session.sendTextInput(text)
            } else {
                session.sendTextEditing("", cursorBegin: 0, cursorEnd: 0)
            }
            return
        }

        let removed = clampedSubstring(target)
        textStorage.replaceCharacters(in: target, with: text)
        markedRangeStorage = nil
        selectedRangeStorage = NSRange(location: target.location + (text as NSString).length, length: 0)
        trimRollingBufferIfNeeded()

        for _ in 0..<removed.count {
            session.sendKeyTap(evdevKey: 14)
        }
        if text == "\n" {
            session.sendKeyTap(evdevKey: 28)
        } else if !text.isEmpty {
            session.sendTextInput(text)
        }
    }

    var selectedTextRange: UITextRange? {
        get { PodishTextRange(clampRange(selectedRangeStorage)) }
        set {
            if let r = newValue as? PodishTextRange {
                selectedRangeStorage = clampRange(r.range)
            }
        }
    }

    var markedTextRange: UITextRange? {
        guard let marked = markedRangeStorage else { return nil }
        let clamped = clampRange(marked)
        return clamped.length > 0 ? PodishTextRange(clamped) : nil
    }

    var beginningOfDocument: UITextPosition { PodishTextPosition(0) }
    var endOfDocument: UITextPosition { PodishTextPosition(textStorage.length) }

    func textRange(from fromPosition: UITextPosition, to toPosition: UITextPosition) -> UITextRange? {
        guard let a = fromPosition as? PodishTextPosition,
              let b = toPosition as? PodishTextPosition else { return nil }
        let lo = max(0, min(min(a.offset, b.offset), textStorage.length))
        let hi = max(0, min(max(a.offset, b.offset), textStorage.length))
        return PodishTextRange(NSRange(location: lo, length: hi - lo))
    }

    func position(from position: UITextPosition, offset: Int) -> UITextPosition? {
        guard let p = position as? PodishTextPosition else { return nil }
        let next = p.offset + offset
        guard next >= 0 && next <= textStorage.length else { return nil }
        return PodishTextPosition(next)
    }

    func position(from position: UITextPosition, in direction: UITextLayoutDirection, offset: Int) -> UITextPosition? {
        switch direction {
        case .left, .up:
            return self.position(from: position, offset: -offset)
        case .right, .down:
            return self.position(from: position, offset: offset)
        @unknown default:
            return nil
        }
    }

    func compare(_ position: UITextPosition, to other: UITextPosition) -> ComparisonResult {
        guard let a = position as? PodishTextPosition,
              let b = other as? PodishTextPosition else { return .orderedSame }
        if a.offset < b.offset { return .orderedAscending }
        if a.offset > b.offset { return .orderedDescending }
        return .orderedSame
    }

    func offset(from: UITextPosition, to toPosition: UITextPosition) -> Int {
        guard let a = from as? PodishTextPosition,
              let b = toPosition as? PodishTextPosition else { return 0 }
        return b.offset - a.offset
    }

    func position(within range: UITextRange, farthestIn direction: UITextLayoutDirection) -> UITextPosition? {
        guard let r = range as? PodishTextRange else { return nil }
        switch direction {
        case .left, .up:
            return PodishTextPosition(r.range.location)
        case .right, .down:
            return PodishTextPosition(r.range.location + r.range.length)
        @unknown default:
            return PodishTextPosition(r.range.location)
        }
    }

    func characterRange(byExtending position: UITextPosition, in direction: UITextLayoutDirection) -> UITextRange? {
        guard let p = position as? PodishTextPosition else { return nil }
        let clamped = max(0, min(p.offset, textStorage.length))
        switch direction {
        case .left, .up:
            return PodishTextRange(NSRange(location: 0, length: clamped))
        case .right, .down:
            return PodishTextRange(NSRange(location: clamped, length: textStorage.length - clamped))
        @unknown default:
            return PodishTextRange(NSRange(location: clamped, length: 0))
        }
    }

    func baseWritingDirection(for position: UITextPosition, in direction: UITextStorageDirection) -> NSWritingDirection {
        .leftToRight
    }

    func setBaseWritingDirection(_ writingDirection: NSWritingDirection, for range: UITextRange) {}

    func firstRect(for range: UITextRange) -> CGRect {
        currentCaretRect()
    }

    func caretRect(for position: UITextPosition) -> CGRect {
        currentCaretRect()
    }

    func selectionRects(for range: UITextRange) -> [UITextSelectionRect] { [] }

    func closestPosition(to point: CGPoint) -> UITextPosition? {
        PodishTextPosition(textStorage.length)
    }

    func closestPosition(to point: CGPoint, within range: UITextRange) -> UITextPosition? {
        range.end
    }

    func characterRange(at point: CGPoint) -> UITextRange? {
        PodishTextRange(NSRange(location: textStorage.length, length: 0))
    }

    private func currentCaretRect() -> CGRect {
        if session.hasValidTextInputRect {
            let r = session.desktopRectToHost(session.textInputRect, in: bounds.size)
            return CGRect(x: r.origin.x, y: r.origin.y, width: max(2, r.width), height: max(18, r.height))
        }
        if let tap = lastTapHostPoint {
            return CGRect(x: tap.x, y: max(0, tap.y - 11), width: 2, height: 22)
        }
        return CGRect(x: 0, y: 0, width: 2, height: 22)
    }

    // MARK: Hardware Keyboard (iPad Magic Keyboard / Bluetooth)

    private func shouldForwardHardwareKeyToIme(_ key: UIKey) -> Bool {
        guard session.textInputActive else { return false }
        let flags = key.modifierFlags
        if flags.contains(.control) || flags.contains(.command) || flags.contains(.alternate) {
            return false
        }
        if markedRangeStorage != nil {
            return true
        }
        let usage = key.keyCode.rawValue
        // Printable keys (letters, numbers, punctuation, space) and Backspace/Return go through UIKit IME.
        return (usage >= 0x04 && usage <= 0x28) || (usage >= 0x2A && usage <= 0x38)
    }

    override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        var unhandled = Set<UIPress>()
        for press in presses {
            guard let key = press.key else {
                unhandled.insert(press)
                continue
            }
            if shouldForwardHardwareKeyToIme(key) {
                unhandled.insert(press)
                continue
            }
            if let evdev = PodishEvdevKeyMap.fromHidUsage(key.keyCode.rawValue) {
                session.sendKey(evdevKey: evdev, pressed: true, handledByIme: false)
            } else {
                unhandled.insert(press)
            }
        }
        if !unhandled.isEmpty {
            super.pressesBegan(unhandled, with: event)
        }
    }

    override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        var unhandled = Set<UIPress>()
        for press in presses {
            guard let key = press.key else {
                unhandled.insert(press)
                continue
            }
            if shouldForwardHardwareKeyToIme(key) {
                unhandled.insert(press)
                continue
            }
            if let evdev = PodishEvdevKeyMap.fromHidUsage(key.keyCode.rawValue) {
                session.sendKey(evdevKey: evdev, pressed: false, handledByIme: false)
            } else {
                unhandled.insert(press)
            }
        }
        if !unhandled.isEmpty {
            super.pressesEnded(unhandled, with: event)
        }
    }
}
#endif
