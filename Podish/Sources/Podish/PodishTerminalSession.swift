import Foundation
import SwiftUI
@preconcurrency import SwiftTerm

private final class NativeCallbackBox: @unchecked Sendable {
    let onLog: @Sendable (String) -> Void
    let onState: @Sendable ([NativeContainerListItem]) -> Void

    init(
        onLog: @escaping @Sendable (String) -> Void,
        onState: @escaping @Sendable ([NativeContainerListItem]) -> Void
    ) {
        self.onLog = onLog
        self.onState = onState
    }
}

private let podishLogCallbackImpl: PodLogCallback = { userData, _, data, len in
    guard let userData, let data, len > 0 else { return }
    let box = Unmanaged<NativeCallbackBox>.fromOpaque(userData).takeUnretainedValue()
    let bytes = UnsafeBufferPointer(start: data, count: Int(len))
    let line = String(decoding: bytes, as: UTF8.self)
    if !line.isEmpty {
        box.onLog(line)
    }
}

private let podishContainerStateCallbackImpl: PodContainerStateCallback = { userData, data, len in
    guard let userData, let data, len > 0 else { return }
    let box = Unmanaged<NativeCallbackBox>.fromOpaque(userData).takeUnretainedValue()
    let payload = Data(bytes: data, count: Int(len))
    guard let items = try? JSONDecoder().decode([NativeContainerListItem].self, from: payload) else { return }
    box.onState(items)
}

private struct PodishRunSpec: Codable {
    var image: String?
    var rootfs: String?
    var exe: String?
    var exeArgs: [String] = []
    var volumes: [String] = []
    var env: [String] = []
    var dns: [String] = []
    var interactive: Bool = true
    var tty: Bool = true
    var strace: Bool = false
    var logDriver: String = "json-file"
}

enum PodishRuntimeError: LocalizedError {
    case native(code: Int32, message: String)

    var errorDescription: String? {
        switch self {
        case .native(let code, let message):
            return "Native error \(code): \(message)"
        }
    }
}

final class TerminalDelegateAdapter: NSObject, TerminalViewDelegate {
    var onResize: ((Int, Int) -> Void)?
    var onInput: (([UInt8]) -> Void)?

    func sizeChanged(source: TerminalView, newCols: Int, newRows: Int) {
        onResize?(newCols, newRows)
    }

    func setTerminalTitle(source: TerminalView, title: String) {}
    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}

    func send(source: TerminalView, data: ArraySlice<UInt8>) {
        onInput?(Array(data))
    }

    func scrolled(source: TerminalView, position: Double) {}
    func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {}
    func bell(source: TerminalView) {}
    func clipboardCopy(source: TerminalView, content: Data) {}
    func iTermContent(source: TerminalView, content: ArraySlice<UInt8>) {}
    func rangeChanged(source: TerminalView, startY: Int, endY: Int) {}
}

actor PodishRuntimeActor {
    private struct RuntimeTerminalSession {
        var container: UnsafeMutableRawPointer
        var terminal: UnsafeMutableRawPointer
        var readTask: Task<Void, Never>?
    }

    private let workDir: String
    private var ctx: UnsafeMutableRawPointer?
    private var callbackUserData: UnsafeMutableRawPointer?

    private var sessions: [String: RuntimeTerminalSession] = [:]
    private var onOutput: (@Sendable (String, [UInt8]) -> Void)?
    private var onLogLine: (@Sendable (String) -> Void)?
    private var onContainerState: (@Sendable ([NativeContainerListItem]) -> Void)?

    struct StartupState {
        let containers: [NativeContainerListItem]
        let attachedContainerId: String?
    }

    init(workDir: String) {
        self.workDir = workDir
    }

    func setObservers(
        onOutput: @escaping @Sendable (String, [UInt8]) -> Void,
        onLog: @escaping @Sendable (String) -> Void,
        onContainerState: @escaping @Sendable ([NativeContainerListItem]) -> Void
    ) {
        self.onOutput = onOutput
        self.onLogLine = onLog
        self.onContainerState = onContainerState
        guard ctx != nil else { return }
        installCallbacks()
    }

    func startDefaultShell() throws -> StartupState {
        try ensureContext()
        var containers = try listContainers()
        var attached: String?

        if containers.isEmpty {
            try pullImageInternal("docker.io/i386/alpine:latest")
            _ = try createAndStartContainer(imageRef: "docker.io/i386/alpine:latest")
            containers = try listContainers()
            attached = containers.first(where: { $0.running })?.containerId ?? containers.first?.containerId
            if let attached {
                try ensureTerminalSession(containerId: attached)
            }
        }

        return StartupState(containers: containers, attachedContainerId: attached)
    }

    func refreshContainers() throws -> [NativeContainerListItem] {
        try ensureContext()
        return try listContainers()
    }

    func refreshImages() throws -> [NativeImageListItem] {
        try ensureContext()
        return try listImages()
    }

    func pullImage(imageRef: String) throws -> [NativeImageListItem] {
        try ensureContext()
        try pullImageInternal(imageRef)
        return try listImages()
    }

    func removeImage(imageRef: String) throws -> [NativeImageListItem] {
        try ensureContext()
        let rc = imageRef.withCString { pod_image_remove(ctx, $0, 1) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return try listImages()
    }

    func createContainer(imageRef: String) throws -> String {
        try ensureContext()
        return try createAndStartContainer(imageRef: imageRef)
    }

    func startContainer(containerId: String) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_start(handle)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return try listContainers()
    }

    func stopContainer(containerId: String) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_stop(handle, 15, 2000)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        closeTerminalSession(containerId: containerId)
        return try listContainers()
    }

    func removeContainer(containerId: String) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_remove(handle, 1)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        closeTerminalSession(containerId: containerId)
        return try listContainers()
    }

    func ensureTerminalSession(containerId: String) throws {
        try ensureContext()
        if sessions[containerId] != nil { return }

        let container = try openContainer(containerId: containerId)
        var terminal: UnsafeMutableRawPointer?
        let rc = pod_terminal_attach(container, &terminal)
        if rc != 0 || terminal == nil {
            pod_container_close(container)
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        sessions[containerId] = RuntimeTerminalSession(container: container, terminal: terminal!, readTask: nil)
        startReadLoop(containerId: containerId)
    }

    func closeTerminalSession(containerId: String) {
        guard let session = sessions.removeValue(forKey: containerId) else { return }
        session.readTask?.cancel()
        pod_terminal_close(session.terminal)
        pod_container_close(session.container)
    }

    func writeInput(containerId: String, _ bytes: [UInt8]) throws {
        guard !bytes.isEmpty, let term = sessions[containerId]?.terminal else { return }

        var offset = 0
        while offset < bytes.count {
            var wrote: Int32 = 0
            let rc = bytes.withUnsafeBytes { raw in
                guard let base = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else {
                    return Int32(22)
                }
                return pod_terminal_write(term, base.advanced(by: offset), Int32(bytes.count - offset), &wrote)
            }
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: lastError())
            }
            if wrote <= 0 { break }
            offset += Int(wrote)
        }
    }

    func resize(containerId: String, cols: Int, rows: Int) {
        guard let term = sessions[containerId]?.terminal else { return }
        let safeRows = UInt16(max(0, min(65535, rows)))
        let safeCols = UInt16(max(0, min(65535, cols)))
        _ = pod_terminal_resize(term, safeRows, safeCols)
    }

    func stop() {
        for (containerId, _) in sessions {
            closeTerminalSession(containerId: containerId)
        }
        sessions.removeAll()

        uninstallCallbacks()
        if let c = ctx {
            pod_ctx_destroy(c)
            ctx = nil
        }
    }

    private func startReadLoop(containerId: String) {
        guard var session = sessions[containerId], session.readTask == nil else { return }

        let task = Task { [weak self] in
            guard let self else { return }
            await self.readLoop(containerId: containerId)
        }
        session.readTask = task
        sessions[containerId] = session
    }

    private func readLoop(containerId: String) async {
        while !Task.isCancelled {
            guard let term = sessions[containerId]?.terminal else { return }
            let termAddress = UInt(bitPattern: term)

            let (rc, chunk) = await Task.detached(priority: .utility) { () -> (Int32, [UInt8]) in
                guard let termPtr = UnsafeMutableRawPointer(bitPattern: termAddress) else {
                    return (Int32(22), [])
                }
                var localBuffer = [UInt8](repeating: 0, count: 4096)
                var count: Int32 = 0
                let rc = localBuffer.withUnsafeMutableBufferPointer { ptr in
                    pod_terminal_read(termPtr, ptr.baseAddress, Int32(ptr.count), 200, &count)
                }
                if rc != 0 || count <= 0 {
                    return (rc, [])
                }
                return (rc, Array(localBuffer.prefix(Int(count))))
            }.value

            if rc != 0 {
                try? await Task.sleep(nanoseconds: 100_000_000)
                continue
            }

            if !chunk.isEmpty {
                onOutput?(containerId, chunk)
            }
        }
    }

    private func ensureContext() throws {
        if ctx != nil { return }

        var out: UnsafeMutableRawPointer?
        let rc = workDir.withCString { cwd in
            "warn".withCString { level in
                var opts = PodCtxOptionsNative(work_dir_utf8: cwd, log_level_utf8: level, log_file_utf8: nil)
                return pod_ctx_create(&opts, &out)
            }
        }

        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: "failed to create context")
        }

        ctx = out
        installCallbacks()
    }

    private func installCallbacks() {
        guard let c = ctx else { return }
        uninstallCallbacks()

        let onLog = onLogLine ?? { _ in }
        let onState = onContainerState ?? { _ in }
        let box = NativeCallbackBox(onLog: onLog, onState: onState)
        let retained = Unmanaged.passRetained(box)
        let userData = retained.toOpaque()

        let rcLog = pod_ctx_set_log_callback(c, podishLogCallbackImpl, userData)
        if rcLog != 0 {
            retained.release()
            return
        }

        let rcState = pod_ctx_set_container_state_callback(c, podishContainerStateCallbackImpl, userData)
        if rcState != 0 {
            _ = pod_ctx_set_log_callback(c, nil, nil)
            retained.release()
            return
        }

        callbackUserData = userData
    }

    private func uninstallCallbacks() {
        guard let c = ctx else { return }
        _ = pod_ctx_set_log_callback(c, nil, nil)
        _ = pod_ctx_set_container_state_callback(c, nil, nil)

        if let userData = callbackUserData {
            Unmanaged<NativeCallbackBox>.fromOpaque(userData).release()
            callbackUserData = nil
        }
    }

    private func pullImageInternal(_ image: String) throws {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let rc = image.withCString { pod_image_pull(c, $0) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func listContainers() throws -> [NativeContainerListItem] {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = buffer.withUnsafeMutableBufferPointer { ptr in
                pod_container_list_json(c, ptr.baseAddress, Int32(ptr.count), &outLen)
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                if n == 0 {
                    return []
                }
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode([NativeContainerListItem].self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "container list too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func listImages() throws -> [NativeImageListItem] {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = buffer.withUnsafeMutableBufferPointer { ptr in
                pod_image_list_json(c, ptr.baseAddress, Int32(ptr.count), &outLen)
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                if n == 0 {
                    return []
                }
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode([NativeImageListItem].self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "image list too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func createAndStartContainer(imageRef: String) throws -> String {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let beforeIds = Set(try listContainers().map(\.containerId))
        let spec = PodishRunSpec(
            image: imageRef,
            rootfs: nil,
            exe: "/bin/ash",
            exeArgs: ["-i"],
            volumes: [],
            env: [],
            dns: [],
            interactive: true,
            tty: true,
            strace: false,
            logDriver: "json-file"
        )
        let data = try JSONEncoder().encode(spec)
        guard let json = String(data: data, encoding: .utf8) else {
            throw PodishRuntimeError.native(code: -1, message: "encode run spec failed")
        }

        var outContainer: UnsafeMutableRawPointer?
        let rcCreate = json.withCString { pod_container_create_json(c, $0, &outContainer) }
        if rcCreate != 0 {
            throw PodishRuntimeError.native(code: rcCreate, message: lastError())
        }
        guard let outContainer else {
            throw PodishRuntimeError.native(code: -1, message: "container handle nil")
        }
        defer { pod_container_close(outContainer) }

        let rcStart = pod_container_start(outContainer)
        if rcStart != 0 {
            throw PodishRuntimeError.native(code: rcStart, message: lastError())
        }

        let after = try listContainers()
        if let created = after.first(where: { !beforeIds.contains($0.containerId) }) {
            return created.containerId
        }
        if let running = after.first(where: { $0.running && $0.image == imageRef }) {
            return running.containerId
        }

        throw PodishRuntimeError.native(code: -1, message: "failed to resolve created container id")
    }

    private func openContainer(containerId: String) throws -> UnsafeMutableRawPointer {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        var outContainer: UnsafeMutableRawPointer?
        let rc = containerId.withCString { pod_container_open(c, $0, &outContainer) }
        if rc != 0 || outContainer == nil {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return outContainer!
    }

    private func lastError() -> String {
        guard let c = ctx else { return "unknown" }
        var buf = [UInt8](repeating: 0, count: 1024)
        _ = pod_ctx_last_error(c, &buf, Int32(buf.count))
        let end = buf.firstIndex(of: 0) ?? buf.count
        return String(decoding: buf[..<end], as: UTF8.self)
    }
}

@MainActor
final class PodishTerminalSession: ObservableObject {
    private let placeholderView: TerminalView
    #if os(macOS)
    private let displayView: TerminalView
    #endif
    private let runtime: PodishRuntimeActor
    private var terminalViews: [String: TerminalView] = [:]
    private var terminalDelegates: [String: TerminalDelegateAdapter] = [:]
    #if os(macOS)
    private var displayDelegate: TerminalDelegateAdapter?
    #endif

    @Published var startupError: String?
    @Published private(set) var activeContainerId: String?

    private var started = false
    var onContainerList: (([NativeContainerListItem]) -> Void)?
    var onContainerStateChanged: (([NativeContainerListItem]) -> Void)?
    var onImageList: (([NativeImageListItem]) -> Void)?
    private var lastContainerSnapshot: [NativeContainerListItem] = []

    var currentTerminalView: TerminalView {
        #if os(macOS)
        displayView
        #else
        if let id = activeContainerId, let view = terminalViews[id] {
            return view
        }
        return placeholderView
        #endif
    }

    var activeTerminalIdentity: String {
        #if os(macOS)
        "__display__"
        #else
        activeContainerId ?? "__placeholder__"
        #endif
    }

    init() {
        #if os(macOS)
        placeholderView = PodishTerminalView(frame: .zero)
        displayView = PodishTerminalView(frame: .zero)
        #else
        placeholderView = TerminalView(frame: .zero)
        #endif
        runtime = PodishRuntimeActor(workDir: PodishTerminalSession.makeWorkDir())
        #if os(macOS)
        configureDisplayDelegate()
        displayView.terminal = placeholderView.terminal
        #endif

        Task { [weak self] in
            guard let self else { return }
            await runtime.setObservers(
                onOutput: { [weak self] containerId, bytes in
                    guard let self else { return }
                    Task { @MainActor in
                        self.ensureTerminalView(containerId: containerId)
                        #if os(macOS)
                        if self.isContainerDisplayed(containerId),
                           self.displayView.terminal === self.terminalViews[containerId]?.terminal {
                            self.displayView.feed(byteArray: bytes[...])
                        } else {
                            self.terminalViews[containerId]?.feed(byteArray: bytes[...])
                        }
                        #else
                        self.terminalViews[containerId]?.feed(byteArray: bytes[...])
                        #endif
                    }
                },
                onLog: { line in
                    PodishLog.core(line)
                },
                onContainerState: { [weak self] items in
                    guard let self else { return }
                    Task { @MainActor in
                        self.lastContainerSnapshot = items
                        self.reconcileRunningSessions(items)
                        self.onContainerList?(items)
                        self.onContainerStateChanged?(items)
                    }
                }
            )
        }
    }

    func startIfNeeded() {
        guard !started else { return }
        started = true

        Task {
            do {
                let state = try await runtime.startDefaultShell()
                let images = try await runtime.refreshImages()
                DispatchQueue.main.async {
                    self.lastContainerSnapshot = state.containers
                    self.reconcileRunningSessions(state.containers)
                    self.onContainerList?(state.containers)
                    self.onContainerStateChanged?(state.containers)
                    self.onImageList?(images)
                    if let attached = state.attachedContainerId {
                        self.attachContainer(attached)
                    }
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                }
            }
        }
    }

    func stop() {
        Task {
            await runtime.stop()
        }
    }

    func refreshContainerList() {
        Task {
            do {
                let items = try await runtime.refreshContainers()
                DispatchQueue.main.async {
                    self.lastContainerSnapshot = items
                    self.reconcileRunningSessions(items)
                    self.onContainerList?(items)
                    self.onContainerStateChanged?(items)
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func refreshImageList() {
        Task {
            do {
                let items = try await runtime.refreshImages()
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func startContainer(_ containerId: String, completion: ((Bool) -> Void)? = nil) {
        Task {
            do {
                _ = try await runtime.startContainer(containerId: containerId)
                try await runtime.ensureTerminalSession(containerId: containerId)
                DispatchQueue.main.async {
                    self.ensureTerminalView(containerId: containerId)
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                    completion?(false)
                }
            }
        }
    }

    func stopContainer(_ containerId: String, completion: ((Bool) -> Void)? = nil) {
        Task {
            do {
                _ = try await runtime.stopContainer(containerId: containerId)
                DispatchQueue.main.async {
                    // Separate the next session prompt from the previous line content.
                    self.terminalViews[containerId]?.feed(byteArray: ArraySlice([0x0D, 0x0A]))
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                    completion?(false)
                }
            }
        }
    }

    func removeContainer(_ containerId: String) {
        Task {
            do {
                _ = try await runtime.removeContainer(containerId: containerId)
                DispatchQueue.main.async {
                    if self.activeContainerId == containerId {
                        self.activeContainerId = nil
                        #if os(macOS)
                        self.displayView.terminal = self.placeholderView.terminal
                        self.requestDisplayRefresh()
                        #endif
                    }
                    self.terminalViews.removeValue(forKey: containerId)
                    self.terminalDelegates.removeValue(forKey: containerId)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func pullImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.pullImage(imageRef: imageRef)
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func removeImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.removeImage(imageRef: imageRef)
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func createContainer(from imageRef: String) {
        Task {
            do {
                let previousActiveId = await MainActor.run { self.activeContainerId }
                let containerId = try await runtime.createContainer(imageRef: imageRef)
                let containers = try await runtime.refreshContainers()
                let keepCurrentActive = previousActiveId != nil
                    && containers.contains(where: { $0.containerId == previousActiveId && $0.running })

                if !keepCurrentActive {
                    try await runtime.ensureTerminalSession(containerId: containerId)
                }

                DispatchQueue.main.async {
                    self.lastContainerSnapshot = containers
                    self.reconcileRunningSessions(containers)
                    self.onContainerList?(containers)
                    self.onContainerStateChanged?(containers)
                    if !keepCurrentActive {
                        self.activateContainer(containerId)
                    }
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func attachContainer(_ containerId: String) {
        #if os(macOS)
        if activeContainerId == containerId, isContainerDisplayed(containerId) { return }
        #else
        if activeContainerId == containerId { return }
        #endif
        ensureTerminalView(containerId: containerId)
        Task {
            do {
                try await runtime.ensureTerminalSession(containerId: containerId)
                DispatchQueue.main.async {
                    self.activateContainer(containerId)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    private func ensureTerminalView(containerId: String) {
        if terminalViews[containerId] != nil { return }

        let terminalView: TerminalView
        #if os(macOS)
        terminalView = PodishTerminalView(frame: .zero)
        #else
        terminalView = TerminalView(frame: .zero)
        #endif

        let delegate = TerminalDelegateAdapter()
        delegate.onInput = { [weak self] data in
            guard let self else { return }
            Task {
                do {
                    try await self.runtime.writeInput(containerId: containerId, data)
                } catch {
                    await MainActor.run { self.startupError = error.localizedDescription }
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            guard let self else { return }
            Task {
                await self.runtime.resize(containerId: containerId, cols: cols, rows: rows)
            }
        }

        terminalView.terminalDelegate = delegate
        terminalViews[containerId] = terminalView
        terminalDelegates[containerId] = delegate
    }

    private func reconcileRunningSessions(_ items: [NativeContainerListItem]) {
        let runningIds = Set(items.filter { $0.running }.map(\.containerId))
        let allIds = Set(items.map(\.containerId))

        for id in runningIds {
            ensureTerminalView(containerId: id)
            Task {
                try? await runtime.ensureTerminalSession(containerId: id)
            }
        }

        for (id, _) in terminalViews where !allIds.contains(id) {
            terminalViews.removeValue(forKey: id)
            terminalDelegates.removeValue(forKey: id)
            if activeContainerId == id {
                activeContainerId = nil
                #if os(macOS)
                displayView.terminal = placeholderView.terminal
                requestDisplayRefresh()
                #endif
            }
            Task {
                await runtime.closeTerminalSession(containerId: id)
            }
        }

        if let active = activeContainerId, !allIds.contains(active) {
            activeContainerId = nil
            #if os(macOS)
            displayView.terminal = placeholderView.terminal
            requestDisplayRefresh()
            #endif
        }
    }

    #if os(macOS)
    private func configureDisplayDelegate() {
        let delegate = TerminalDelegateAdapter()
        delegate.onInput = { [weak self] data in
            guard let self else { return }
            guard let active = self.activeContainerId else { return }
            Task {
                do {
                    try await self.runtime.writeInput(containerId: active, data)
                } catch {
                    await MainActor.run { self.startupError = error.localizedDescription }
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            guard let self else { return }
            guard let active = self.activeContainerId else { return }
            Task {
                await self.runtime.resize(containerId: active, cols: cols, rows: rows)
            }
        }
        displayView.terminalDelegate = delegate
        displayDelegate = delegate
    }
    #endif

    private func activateContainer(_ containerId: String) {
        let previousActiveId = activeContainerId
        ensureTerminalView(containerId: containerId)
        if let previousActiveId,
           let previousView = terminalViews[previousActiveId] {
            clearTransientViewState(previousView)
        }
        activeContainerId = containerId
        #if os(macOS)
        if let term = terminalViews[containerId]?.terminal {
            if let currentView = terminalViews[containerId] {
                clearTransientViewState(currentView)
            }
            clearTransientViewState(displayView)
            displayView.terminal = term
            // Re-emit current terminal geometry and force a display pass so caret/scroller
            // are recomputed immediately after switching buffers.
            displayView.sizeChanged(source: term)
            displayView.feed(byteArray: ArraySlice<UInt8>())
            requestDisplayRefresh()
        }
        #endif
    }

    #if os(macOS)
    private func isContainerDisplayed(_ containerId: String) -> Bool {
        guard activeContainerId == containerId else { return false }
        return terminalViews[containerId]?.terminal === displayView.terminal
    }
    #endif

    private func requestDisplayRefresh() {
        #if os(macOS)
        displayView.needsDisplay = true
        #else
        currentTerminalView.setNeedsDisplay()
        #endif
    }

    private func clearTransientViewState(_ view: TerminalView) {
        view.selectNone()
        view.clearSearch()
    }

    private static func makeWorkDir() -> String {
        let fm = FileManager.default
        let base = (try? fm.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? URL(fileURLWithPath: fm.currentDirectoryPath)
        let dir = base.appendingPathComponent("Podish", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.path
    }
}

#if os(macOS)
import AppKit

struct TerminalViewHost: NSViewRepresentable {
    let terminalView: TerminalView

    func makeNSView(context: Context) -> TerminalView {
        terminalView
    }

    func updateNSView(_ nsView: TerminalView, context: Context) {
        guard let window = nsView.window else { return }
        if !window.isKeyWindow {
            window.makeKeyAndOrderFront(nil)
        }
        if window.firstResponder !== nsView {
            _ = window.makeFirstResponder(nsView)
        }
    }
}
#else
import UIKit

struct TerminalViewHost: UIViewRepresentable {
    let terminalView: TerminalView

    func makeUIView(context: Context) -> TerminalView {
        terminalView
    }

    func updateUIView(_ uiView: TerminalView, context: Context) {}
}
#endif
