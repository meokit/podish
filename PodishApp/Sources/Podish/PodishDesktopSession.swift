import Foundation
import QuartzCore

private func podishOnTextInputChangedCallback(
    userData: UnsafeMutableRawPointer?,
    active: Int32,
    x: Int32,
    y: Int32,
    width: Int32,
    height: Int32
) {
    guard let userData else { return }
    let session = Unmanaged<PodishDesktopSession>.fromOpaque(userData).takeUnretainedValue()
    let isActive = active != 0
    let rect = CGRect(x: Int(x), y: Int(y), width: Int(width), height: Int(height))
    DispatchQueue.main.async {
        session.handleTextInputChanged(active: isActive, rect: rect)
    }
}

private func podishOnCursorChangedCallback(
    userData: UnsafeMutableRawPointer?,
    shape: Int32
) {
    guard let userData else { return }
    let session = Unmanaged<PodishDesktopSession>.fromOpaque(userData).takeUnretainedValue()
    DispatchQueue.main.async {
        session.handleCursorChanged(shape: shape)
    }
}

private func podishOnStatusChangedCallback(
    userData: UnsafeMutableRawPointer?,
    code: Int32,
    messagePtr: UnsafePointer<CChar>?
) {
    guard let userData else { return }
    let session = Unmanaged<PodishDesktopSession>.fromOpaque(userData).takeUnretainedValue()
    let msg = messagePtr.map { String(cString: $0) } ?? ""
    DispatchQueue.main.async {
        session.handleStatusChanged(code: code, message: msg)
    }
}

private func podishOnVmSettingsChangedCallback(
    userData: UnsafeMutableRawPointer?,
    memoryLimitMb: Int32,
    blockCacheMb: Int32
) {
    guard let userData else { return }
    let session = Unmanaged<PodishDesktopSession>.fromOpaque(userData).takeUnretainedValue()
    DispatchQueue.main.async {
        session.handleVmSettingsChanged(memoryLimitMb: Int(memoryLimitMb), blockCacheMb: Int(blockCacheMb))
    }
}

enum PodishHostPreferences {
    private static let memoryLimitKey = "podish.vm.memoryLimitMb"
    private static let blockCacheKey = "podish.vm.blockCacheMb"

    static var vmMemoryLimitMb: Int {
        get { max(0, UserDefaults.standard.integer(forKey: memoryLimitKey)) }
        set { UserDefaults.standard.set(max(0, newValue), forKey: memoryLimitKey) }
    }

    static var vmBlockCacheMb: Int {
        get { max(0, UserDefaults.standard.integer(forKey: blockCacheKey)) }
        set { UserDefaults.standard.set(max(0, newValue), forKey: blockCacheKey) }
    }
}

enum PodishClientDpiPreset: Int, CaseIterable, Identifiable {
    case compact = 0   // 1.4x (134 DPI)
    case balanced = 1  // 1.75x (168 DPI)
    case standard = 2  // 2.0x (192 DPI)
    case large = 3     // 2.5x (240 DPI)

    var id: Int { rawValue }

    var clientDpi: Int32 {
        switch self {
        case .compact: return 134
        case .balanced: return 168
        case .standard: return 192
        case .large: return 240
        }
    }

    var shortLabel: String {
        switch self {
        case .compact: return "1.4x"
        case .balanced: return "1.75x"
        case .standard: return "2.0x"
        case .large: return "2.5x"
        }
    }

    func next() -> PodishClientDpiPreset {
        let all = Self.allCases
        return all[(rawValue + 1) % all.count]
    }
}

@MainActor
final class PodishDesktopSession: ObservableObject {
    enum State: Equatable {
        case idle
        case starting(String)
        case running
        case exited(String)
        case failed(String)
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var textInputActive = false
    @Published private(set) var textInputRect: CGRect = .zero
    @Published private(set) var hasValidTextInputRect = false
    @Published private(set) var keyboardVisible = false
    @Published private(set) var cursorShape: Int32 = 0
    @Published var rightClickMode = false
    @Published var ctrlSticky = false
    @Published private(set) var trackpadMode = false
    @Published private(set) var dpiPreset: PodishClientDpiPreset = .balanced
    @Published private(set) var vmMemoryLimitMb: Int = PodishHostPreferences.vmMemoryLimitMb
    @Published private(set) var vmBlockCacheMb: Int = PodishHostPreferences.vmBlockCacheMb

    private var handle: UnsafeMutableRawPointer?
    private weak var attachedLayer: CAMetalLayer?
    private var hasInitializedPreset = false
    private var lastHostLogicalSize: CGSize = .zero
    private var lastPixelSize: CGSize = .zero
    private var lastHostScale: CGFloat = 2.0
    private(set) var desktopLogicalSize: CGSize = .zero
    private var retainedSelfPtr: UnsafeMutableRawPointer?

    var onTextInputChanged: ((Bool, CGRect?) -> Void)?
    var onCursorChanged: ((Int32) -> Void)?
    var onToggleKeyboardRequested: (() -> Void)?

    var navBarHeightDesktop: CGFloat {
        #if os(iOS)
        let h = desktopLogicalSize.height > 0 ? desktopLogicalSize.height : lastHostLogicalSize.height
        guard h > 0 else { return 54 }
        return CGFloat(max(48, min(120, Int((h * 0.064).rounded()))))
        #else
        return 0
        #endif
    }

    var navBarHeightHost: CGFloat {
        #if os(iOS)
        guard desktopLogicalSize.height > 0, lastHostLogicalSize.height > 0 else { return 54 }
        return navBarHeightDesktop * lastHostLogicalSize.height / desktopLogicalSize.height
        #else
        return 0
        #endif
    }

    func hostToDesktop(_ point: CGPoint, in viewSize: CGSize) -> (Int32, Int32) {
        let dw = desktopLogicalSize.width > 1 ? desktopLogicalSize.width : viewSize.width
        let dh = desktopLogicalSize.height > 1 ? desktopLogicalSize.height : viewSize.height
        guard viewSize.width > 0, viewSize.height > 0 else { return (0, 0) }
        let nx = max(0, min(1, point.x / viewSize.width))
        let ny = max(0, min(1, point.y / viewSize.height))
        let x = Int32(max(0, min(dw - 1, (nx * dw).rounded())))
        let y = Int32(max(0, min(dh - 1, (ny * dh).rounded())))
        return (x, y)
    }

    func desktopRectToHost(_ rect: CGRect, in viewSize: CGSize) -> CGRect {
        let dw = desktopLogicalSize.width
        let dh = desktopLogicalSize.height
        guard dw > 0, dh > 0, viewSize.width > 0, viewSize.height > 0 else { return rect }
        let sx = viewSize.width / dw
        let sy = viewSize.height / dh
        return CGRect(
            x: rect.origin.x * sx,
            y: rect.origin.y * sy,
            width: rect.width * sx,
            height: rect.height * sy
        )
    }

    func setKeyboardVisible(_ visible: Bool) {
        if keyboardVisible != visible {
            keyboardVisible = visible
        }
    }

    func toggleKeyboard() {
        onToggleKeyboardRequested?()
    }

    func setTrackpadMode(_ enabled: Bool) {
        if trackpadMode != enabled {
            trackpadMode = enabled
        }
        guard let handle else { return }
        pod_desktop_set_trackpad_mode(handle, enabled ? 1 : 0)
    }

    func toggleTrackpadMode() {
        setTrackpadMode(!trackpadMode)
    }

    func cycleClientDpiPreset() {
        dpiPreset = dpiPreset.next()
        hasInitializedPreset = true
        guard let handle else { return }
        pod_desktop_set_global_dpi(handle, dpiPreset.clientDpi)
    }

    func triggerNavAction(_ action: Int32) {
        guard let handle else { return }
        pod_desktop_nav_action(handle, action)
    }

    fileprivate func handleVmSettingsChanged(memoryLimitMb: Int, blockCacheMb: Int) {
        let clampedMem = max(0, memoryLimitMb)
        let clampedCache = max(0, blockCacheMb)
        PodishHostPreferences.vmMemoryLimitMb = clampedMem
        PodishHostPreferences.vmBlockCacheMb = clampedCache
        vmMemoryLimitMb = clampedMem
        vmBlockCacheMb = clampedCache
    }

    private func computeCanvasMetrics(hostLogical: CGSize, pixelSize: CGSize, hostScale: CGFloat)
        -> (logicalW: Int32, logicalH: Int32, pixelW: Int32, pixelH: Int32, scale120: UInt32, initialGlobalDpi: Int32)
    {
        let lw = max(320, Int32(hostLogical.width.rounded()))
        let lh = max(320, Int32(hostLogical.height.rounded()))
        let pw = max(320, Int32(pixelSize.width.rounded()))
        let ph = max(320, Int32(pixelSize.height.rounded()))
        let scale120 = UInt32(max(120, min(480, Int((hostScale * 120.0).rounded()))))
        #if os(iOS)
        let defaultGlobalDpi: Int32 = min(hostLogical.width, hostLogical.height) < 540 ? 168 : 0
        #else
        let defaultGlobalDpi: Int32 = 0
        #endif
        return (lw, lh, pw, ph, scale120, defaultGlobalDpi)
    }

    static func stateDirectoryURL() -> URL {
        let fm = FileManager.default
        let base = (try? fm.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )) ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let dir = base.appendingPathComponent("Podish", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static func bundledRootfsURL() -> URL? {
        if let env = ProcessInfo.processInfo.environment["PODISH_ROOTFS_STORE_DIR"], !env.isEmpty {
            let url = URL(fileURLWithPath: env, isDirectory: true)
            if FileManager.default.fileExists(atPath: url.appendingPathComponent("image.json").path) {
                return url
            }
        }
        if let res = Bundle.main.resourceURL?.appendingPathComponent("PodishRootfs", isDirectory: true),
           FileManager.default.fileExists(atPath: res.appendingPathComponent("image.json").path) {
            return res
        }
        return nil
    }

    static func bundledAngleDirectoryPath() -> String? {
        if let env = ProcessInfo.processInfo.environment["PODISH_ANGLE_DIR"], !env.isEmpty {
            return env
        }
        if let fw = Bundle.main.privateFrameworksPath {
            return fw
        }
        return Bundle.main.resourcePath
    }

    static func bundledGlesGuestDirectoryPath() -> String? {
        if let env = ProcessInfo.processInfo.environment["PODISH_GLES_GUEST_DIR"], !env.isEmpty {
            return env
        }
        if let res = Bundle.main.resourceURL?.appendingPathComponent("PodishGlesGuest", isDirectory: true),
           FileManager.default.fileExists(atPath: res.appendingPathComponent("libEGL.so.1").path) {
            return res.path
        }
        return nil
    }

    func attachAndStart(metalLayer: CAMetalLayer, logicalSize: CGSize, pixelSize: CGSize, scale: CGFloat) {
        attachedLayer = metalLayer
        lastHostLogicalSize = logicalSize
        lastPixelSize = pixelSize
        lastHostScale = scale

        #if os(iOS)
        if !hasInitializedPreset {
            hasInitializedPreset = true
            dpiPreset = min(logicalSize.width, logicalSize.height) < 540 ? .balanced : .standard
        }
        #endif

        if handle != nil {
            resize(logicalSize: logicalSize, pixelSize: pixelSize, scale: scale)
            return
        }

        guard let rootfsURL = Self.bundledRootfsURL() else {
            state = .failed("Embedded Debian rootfs (PodishRootfs) is missing from the app bundle.")
            return
        }
        guard let angleDir = Self.bundledAngleDirectoryPath() else {
            state = .failed("ANGLE Metal frameworks are missing from the app bundle.")
            return
        }

        let stateDir = Self.stateDirectoryURL().path
        let glesGuestDir = Self.bundledGlesGuestDirectoryPath() ?? ""
        let logLevel = ProcessInfo.processInfo.environment["PODISH_LOG_LEVEL"] ?? "info"
        #if os(iOS)
        let isTouch: Int32 = 1
        #else
        let isTouch: Int32 = 0
        #endif

        let m = computeCanvasMetrics(hostLogical: logicalSize, pixelSize: pixelSize, hostScale: scale)
        desktopLogicalSize = CGSize(width: CGFloat(m.logicalW), height: CGFloat(m.logicalH))

        let savedMemMb = Int32(PodishHostPreferences.vmMemoryLimitMb)
        let savedCacheMb = Int32(PodishHostPreferences.vmBlockCacheMb)
        vmMemoryLimitMb = Int(savedMemMb)
        vmBlockCacheMb = Int(savedCacheMb)

        state = .starting("Starting Debian Wayland desktop...")

        let userData = Unmanaged.passRetained(self).toOpaque()
        retainedSelfPtr = userData
        let layerPtr = Unmanaged.passUnretained(metalLayer).toOpaque()

        var outHandle: UnsafeMutableRawPointer?
        let createRc: Int32 = rootfsURL.path.withCString { rootfsC in
            stateDir.withCString { stateC in
                angleDir.withCString { angleC in
                    glesGuestDir.withCString { guestC in
                        logLevel.withCString { logC in
                            var opts = PodDesktopOptionsNative(
                                rootfs_store_dir_utf8: rootfsC,
                                state_dir_utf8: stateC,
                                angle_dir_utf8: angleC,
                                gles_guest_dir_utf8: glesGuestDir.isEmpty ? nil : guestC,
                                log_level_utf8: logC,
                                metal_layer: layerPtr,
                                logical_width: m.logicalW,
                                logical_height: m.logicalH,
                                pixel_width: m.pixelW,
                                pixel_height: m.pixelH,
                                scale_120: m.scale120,
                                enable_gles: glesGuestDir.isEmpty ? 0 : 1,
                                touch_mode: isTouch,
                                on_text_input_changed: podishOnTextInputChangedCallback,
                                on_cursor_changed: podishOnCursorChangedCallback,
                                on_status_changed: podishOnStatusChangedCallback,
                                rasterize_text: podishRasterizeTextCallback,
                                rasterize_icon: podishRasterizeIconCallback,
                                callback_user_data: userData,
                                vm_memory_limit_mb: savedMemMb,
                                vm_block_cache_mb: savedCacheMb,
                                global_client_dpi: m.initialGlobalDpi,
                                on_vm_settings_changed: podishOnVmSettingsChangedCallback
                            )
                            return pod_desktop_create(&opts, &outHandle)
                        }
                    }
                }
            }
        }

        guard createRc == 0, let desktop = outHandle else {
            releaseRetainedSelf()
            state = .failed("Could not create embedded desktop runtime (rc=\(createRc)).")
            return
        }

        handle = desktop
        let startRc = pod_desktop_start(desktop)
        if startRc != 0 {
            let err = readLastError(desktop)
            pod_desktop_destroy(desktop)
            handle = nil
            releaseRetainedSelf()
            state = .failed(err.isEmpty ? "Failed to start embedded desktop (rc=\(startRc))." : err)
            return
        }
        if trackpadMode {
            pod_desktop_set_trackpad_mode(desktop, 1)
        }
    }

    fileprivate func handleTextInputChanged(active: Bool, rect: CGRect) {
        let validRect = active && rect.origin.x >= 0 && rect.origin.y >= 0 && rect.width > 0 && rect.height > 0
        textInputActive = active
        hasValidTextInputRect = validRect
        textInputRect = validRect ? rect : .zero
        onTextInputChanged?(active, validRect ? rect : nil)
    }

    static func utf8ByteOffset(in text: String, utf16Offset: Int) -> Int32 {
        if utf16Offset < 0 { return -1 }
        if utf16Offset == 0 { return 0 }
        var units = 0
        var bytes = 0
        for scalar in text.unicodeScalars {
            units += scalar.utf16.count
            if units > utf16Offset { break }
            bytes += scalar.utf8.count
        }
        return Int32(bytes)
    }

    func sendTextEditingUtf16(_ preedit: String, selectedUtf16Range: NSRange) {
        let beginUtf16 = selectedUtf16Range.location == NSNotFound ? preedit.utf16.count : selectedUtf16Range.location
        let endUtf16 = selectedUtf16Range.location == NSNotFound
            ? beginUtf16
            : beginUtf16 + max(0, selectedUtf16Range.length)
        let beginByte = Self.utf8ByteOffset(in: preedit, utf16Offset: beginUtf16)
        let endByte = Self.utf8ByteOffset(in: preedit, utf16Offset: endUtf16)
        sendTextEditing(preedit, cursorBegin: beginByte, cursorEnd: endByte)
    }

    fileprivate func handleCursorChanged(shape: Int32) {
        cursorShape = shape
        onCursorChanged?(shape)
    }

    fileprivate func handleStatusChanged(code: Int32, message: String) {
        switch code {
        case 1, 2:
            state = .starting(message)
        case 3:
            state = .running
        case 4:
            state = .exited(message)
        default:
            state = .failed(message.isEmpty ? "Desktop runtime error" : message)
        }
    }

    func resize(logicalSize: CGSize, pixelSize: CGSize, scale: CGFloat) {
        lastHostLogicalSize = logicalSize
        lastPixelSize = pixelSize
        lastHostScale = scale
        guard let handle else { return }
        let m = computeCanvasMetrics(hostLogical: logicalSize, pixelSize: pixelSize, hostScale: scale)
        desktopLogicalSize = CGSize(width: CGFloat(m.logicalW), height: CGFloat(m.logicalH))
        pod_desktop_resize(handle, m.logicalW, m.logicalH, m.pixelW, m.pixelH, m.scale120)
    }

    func setVisible(_ visible: Bool) {
        guard let handle else { return }
        pod_desktop_set_visible(handle, visible ? 1 : 0)
    }

    func sendPointerMotion(x: Int32, y: Int32) {
        guard let handle else { return }
        pod_desktop_pointer_motion(handle, x, y, nowMs())
    }

    func sendPointerButton(x: Int32, y: Int32, button: UInt32, pressed: Bool) {
        guard let handle else { return }
        pod_desktop_pointer_button(handle, x, y, button, pressed ? 1 : 0, nowMs())
    }

    func sendPointerWheel(dx: Int32, dy: Int32) {
        guard let handle else { return }
        pod_desktop_pointer_wheel(handle, dx, dy, nowMs())
    }

    func sendPointerLeave() {
        guard let handle else { return }
        pod_desktop_pointer_leave(handle, nowMs())
    }

    func sendKey(evdevKey: UInt32, pressed: Bool, handledByIme: Bool = false) {
        guard let handle else { return }
        pod_desktop_key(handle, evdevKey, pressed ? 1 : 0, handledByIme ? 1 : 0, nowMs())
    }

    func sendKeyTap(evdevKey: UInt32, withShift: Bool = false, withCtrl: Bool = false) {
        let useCtrl = withCtrl || ctrlSticky
        if useCtrl { sendKey(evdevKey: 29, pressed: true) }
        if withShift { sendKey(evdevKey: 42, pressed: true) }
        sendKey(evdevKey: evdevKey, pressed: true)
        sendKey(evdevKey: evdevKey, pressed: false)
        if withShift { sendKey(evdevKey: 42, pressed: false) }
        if useCtrl {
            sendKey(evdevKey: 29, pressed: false)
            ctrlSticky = false
        }
    }

    func sendTextInput(_ text: String) {
        guard let handle, !text.isEmpty else { return }
        if textInputActive && !ctrlSticky {
            text.withCString { ptr in
                pod_desktop_text_input(handle, ptr)
            }
            return
        }
        for ch in text {
            if let stroke = PodishEvdevKeyMap.asciiKeyStroke(ch) {
                sendKeyTap(evdevKey: stroke.evdev, withShift: stroke.shift)
            } else {
                String(ch).withCString { ptr in
                    pod_desktop_text_input(handle, ptr)
                }
            }
        }
    }

    func sendTextEditing(_ preedit: String, cursorBegin: Int32, cursorEnd: Int32) {
        guard let handle else { return }
        preedit.withCString { ptr in
            pod_desktop_text_editing(handle, ptr, cursorBegin, cursorEnd)
        }
    }

    func sendFocusChanged(_ focused: Bool) {
        guard let handle else { return }
        pod_desktop_focus_changed(handle, focused ? 1 : 0)
    }

    func focusLauncher() {
        guard let handle else { return }
        pod_desktop_focus_launcher(handle)
    }

    func stop() {
        guard let desktop = handle else { return }
        handle = nil
        pod_desktop_stop(desktop)
        pod_desktop_destroy(desktop)
        releaseRetainedSelf()
    }

    func stopForAppTermination(completion: @escaping () -> Void) {
        let desktop = handle
        handle = nil
        let retained = retainedSelfPtr
        retainedSelfPtr = nil
        DispatchQueue.global(qos: .userInitiated).async {
            if let desktop {
                pod_desktop_stop(desktop)
                pod_desktop_destroy(desktop)
            }
            if let retained {
                Unmanaged<PodishDesktopSession>.fromOpaque(retained).release()
            }
            DispatchQueue.main.async {
                completion()
            }
        }
    }

    func resetUserDataAndRestart() {
        stop()
        let stateDir = Self.stateDirectoryURL().path
        _ = stateDir.withCString { pod_desktop_reset_overlay($0) }
        if let layer = attachedLayer {
            attachAndStart(metalLayer: layer, logicalSize: lastHostLogicalSize, pixelSize: lastPixelSize, scale: lastHostScale)
        }
    }

    func restart() {
        stop()
        if let layer = attachedLayer {
            attachAndStart(metalLayer: layer, logicalSize: lastHostLogicalSize, pixelSize: lastPixelSize, scale: lastHostScale)
        }
    }

    private func releaseRetainedSelf() {
        if let ptr = retainedSelfPtr {
            retainedSelfPtr = nil
            Unmanaged<PodishDesktopSession>.fromOpaque(ptr).release()
        }
    }

    private func readLastError(_ desktop: UnsafeMutableRawPointer) -> String {
        var buf = [UInt8](repeating: 0, count: 2048)
        let n = pod_desktop_last_error(desktop, &buf, Int32(buf.count))
        if n <= 0 { return "" }
        return String(decoding: buf.prefix(Int(n)), as: UTF8.self)
    }

    private func nowMs() -> UInt32 {
        UInt32(truncatingIfNeeded: UInt64(ProcessInfo.processInfo.systemUptime * 1000.0))
    }
}
